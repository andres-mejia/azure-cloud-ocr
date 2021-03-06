// Copyright (c) Yuriy Guts, 2013
// 
// Licensed under the Apache License, version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at:
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// 
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading;
using AzureStorageUtils;
using AzureStorageUtils.Entities;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using SendGridMail;
using SendGridMail.Transport;

namespace EmailWorkerRole
{
    public class WorkerRole : RoleEntryPoint
    {
        private volatile bool onStopCalled;
        private volatile bool returnedFromRunMethod;
        private Web sendGridTransport;

        private readonly TimeSpan emailQueueMessageVisibilityTimeout;
        private readonly QueueRequestOptions emailQueueRequestOptions;
        private const int maxSingleMessageDequeueCount = 10;

        public WorkerRole()
        {
            emailQueueMessageVisibilityTimeout = TimeSpan.FromMinutes(1);
            emailQueueRequestOptions = new QueueRequestOptions
            {
                MaximumExecutionTime = TimeSpan.FromMinutes(15),
                RetryPolicy = new LinearRetry(TimeSpan.FromMinutes(1), 5)
            };
        }

        public override void Run()
        {
            Trace.TraceInformation("EmailWorkerRole entry point called.", "Information");

            while (true)
            {
                Trace.TraceInformation("EmailWorkerRole is awake.", "Information");
                AzureQueues.EmailQueue.FetchAttributes();
                Trace.TraceInformation("Email Queue has approximately {0} message(s).", AzureQueues.EmailQueue.ApproximateMessageCount ?? 0);

                while (true)
                {
                    if (onStopCalled)
                    {
                        Trace.TraceInformation("OnStop request caught in Run method. Stopping all work.");
                        returnedFromRunMethod = true;
                        return;
                    }

                    var queueMessage = AzureQueues.EmailQueue.GetMessage(emailQueueMessageVisibilityTimeout, emailQueueRequestOptions);
                    if (queueMessage == null)
                    {
                        break;
                    }

                    // To protect from accidental poison messages that get stuck in the queue.
                    if (queueMessage.DequeueCount > maxSingleMessageDequeueCount)
                    {
                        Trace.TraceWarning("Message max dequeue limit reached. Deleting it as a poison message.");
                        AzureQueues.EmailQueue.DeleteMessage(queueMessage);
                        return;
                    }

                    ProcessEmailQueueMessage(queueMessage);
                }

                Trace.TraceInformation("No new messages to process. Sleeping.", "Information");
                Thread.Sleep(10000);
            }
        }

        public override bool OnStart()
        {
            // Set the maximum number of concurrent connections 
            ServicePointManager.DefaultConnectionLimit = 12;

            InitializeAzureStorage();
            InitializeSendGridMailer();

            // For information on handling configuration changes
            // see the MSDN topic at http://go.microsoft.com/fwlink/?LinkId=166357.

            return base.OnStart();
        }

        public override void OnStop()
        {
            Trace.TraceInformation("OnStop request received. Trying to stop...");

            onStopCalled = true;
            while (!returnedFromRunMethod)
            {
                Thread.Sleep(1000);
            }

            Trace.TraceInformation("Ready to stop.");
            base.OnStop();
        }

        private void InitializeAzureStorage()
        {
            Trace.TraceInformation("Initializing Azure Storage.");

            Trace.TraceInformation("Loading storage settings.");
            string storageConnectionString = RoleEnvironment.GetConfigurationSettingValue("StorageConnectionString");
            string textBlobContainerName = RoleEnvironment.GetConfigurationSettingValue("TextBlobContainerName");
            string emailQueueName = RoleEnvironment.GetConfigurationSettingValue("EmailQueueName");
            string ocrJobTableName = RoleEnvironment.GetConfigurationSettingValue("OCRJobTableName");

            Trace.TraceInformation("Initializing Blobs.");
            AzureBlobs.Initialize(storageConnectionString, null, textBlobContainerName);

            Trace.TraceInformation("Initializing Queues.");
            AzureQueues.Initialize(storageConnectionString, null, emailQueueName);

            Trace.TraceInformation("Initializing Tables.");
            AzureTables.Initialize(storageConnectionString, ocrJobTableName);
        }

        private void InitializeSendGridMailer()
        {
            Trace.TraceInformation("Initializing SendGrid mailer.");

            Trace.TraceInformation("Loading settings.");
            string userName = RoleEnvironment.GetConfigurationSettingValue("SendGridUserName");
            string password = RoleEnvironment.GetConfigurationSettingValue("SendGridPassword");

            NetworkCredential sendGridCredentials = new NetworkCredential(userName, password);
            sendGridTransport = Web.GetInstance(sendGridCredentials);
        }

        private void ProcessEmailQueueMessage(CloudQueueMessage queueMessage)
        {
            string messageContent = queueMessage.AsString;
            Trace.TraceInformation("Processing email queue message: " + messageContent);

            EmailQueueMessage emailMessage = null;
            Exception exception = null;

            try
            {
                emailMessage = EmailQueueMessage.Parse(messageContent);
                SendOCRTextByEmail(emailMessage.TextBlobName, emailMessage.RecipientEmail);
                DeleteTextBlob(emailMessage.TextBlobName);
            }
            catch (Exception ex)
            {
                Trace.TraceError("An error occurred while processing email queue message. Details: " + ex);
                exception = ex;

                if (emailMessage == null)
                {
                    Trace.TraceError("Invalid message format. Deleting.");
                    AzureQueues.EmailQueue.DeleteMessage(queueMessage);
                    return;
                }
            }

            try
            {
                var job = AzureTables.OCRJobRepository.GetOCRJob(emailMessage.JobID, emailMessage.RecipientEmail);
                if (exception == null)
                {
                    job.IsCompleted = true;
                    job.ErrorMessage = null;
                    AzureTables.OCRJobRepository.UpdateOCRJob(job);

                    Trace.TraceInformation("Message (JobID = {0}) successfully processed, deleting.", emailMessage.JobID);
                    AzureQueues.EmailQueue.DeleteMessage(queueMessage);
                }
                else
                {
                    job.IsCompleted = true;
                    job.ErrorMessage = exception.ToString();
                    AzureTables.OCRJobRepository.UpdateOCRJob(job);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("Failed to update job status: " + ex);
            }
        }

        private void SendOCRTextByEmail(string textBlobName, string recipientEmail)
        {
            using (MemoryStream attachmentStream = new MemoryStream())
            {
                var blob = AzureBlobs.TextBlobContainer.GetBlockBlobReference(textBlobName);
                blob.DownloadToStream(attachmentStream);
                attachmentStream.Seek(0, SeekOrigin.Begin);

                var message = SendGrid.GetInstance();
                message.From = new MailAddress("azure-cloud-ocr@eleks.com", "Windows Azure Cloud OCR");
                message.To = new[] { new MailAddress(recipientEmail) };
                message.Subject = "Image recognition results";
                message.Text = "Please find your recognized text attached.";
                message.Html = message.Text;

                message.AddAttachment(attachmentStream, "recognized-text.txt");
                sendGridTransport.Deliver(message);
            }
        }

        private void DeleteTextBlob(string textBlobName)
        {
            var textBlob = AzureBlobs.TextBlobContainer.GetBlockBlobReference(textBlobName);
            textBlob.Delete();
        }
    }
}
