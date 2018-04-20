using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.ServiceBus.Messaging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AzureFunctionApp_Demo
{
    public static class MovieMaker
    {
        [FunctionName("MovieMaker")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = "HttpTriggerMovieMaker")]HttpRequestMessage req, TraceWriter log)
        {
            log.Info("C# HTTP trigger function processed a request.");

            // Get request body
            var data = await req.Content.ReadAsStringAsync();
            log.Info(data);

            var response = data.Split('&').Select(s => s.Split(new[] { '=' }));
            Dictionary<string, string> keyVal = new Dictionary<string, string>();
            foreach (var item in response)
            {
                keyVal.Add(item[0], item[1]);
            }

            string channelId = string.Empty;
            string message_ts = string.Empty;

            if (keyVal.ContainsKey("timestamp"))
            {
                message_ts = keyVal["timestamp"];
            }
            if (keyVal.ContainsKey("channel_id"))
            {
                channelId = keyVal["channel_id"];
            }

            if (!string.IsNullOrEmpty(message_ts) && !string.IsNullOrEmpty(channelId))
            {
                var slackclient = new SlackClient();
                var message = await slackclient.getMessages(channelId, message_ts);

                var sbQueueClient = new SBQueueClient();
                await sbQueueClient.EnQueueMessage(message);

                //var deQueuedMessage= await sbQueueClient.DeQueueMessage();

                return req.CreateResponse(HttpStatusCode.OK);
            }
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        public class SlackClient
        {

            public async Task<SlackMessage> getMessages(string channelId, string messageTs)
            {
                string channelHistoryRequestUrl = ConfigurationManager.AppSettings["ChannelHistoryRequestUrl"];
                string slackTokenKeyName= ConfigurationManager.AppSettings["SlackTokenKeyName"];
                AzureKeyVaultClient _client = new AzureKeyVaultClient();
                var _slacktoken = await _client.GetSecret(slackTokenKeyName);


                var requestUrl = string.Concat(channelHistoryRequestUrl, $"?token={_slacktoken}&channel={channelId}&latest={messageTs}&inclusive=true&count=1");
                using (HttpClient client = new HttpClient())
                {
                    var response = await client.GetAsync(new Uri(requestUrl));

                    var content = await response.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(content);

                    var message = ((JArray)result["messages"]).First.ToObject<Dictionary<string, object>>();

                    SlackMessage slackMessage = null;

                    if (message.ContainsKey("file"))
                    {
                        var filedetails = ((JToken)message["file"]).ToObject<Dictionary<string, object>>();
                        var ext = filedetails["filetype"].ToString();
                        if (ext == "jpg")
                        {
                            //Retrieve File Content
                            var fileContent = await getFileContents(filedetails["url_private"].ToString());

                            //Upload file to Blob
                            var blobStorageClient = new BlobStorageClient();
                            var blobPath = await blobStorageClient.UploadDocument(fileContent, Guid.NewGuid().ToString() + "." + ext);
                            slackMessage = new SlackMessage();
                            slackMessage.isFile = true;
                            slackMessage.filePath = blobPath;
                            slackMessage.text = message["text"].ToString();
                        }
                    }
                    else
                    {
                        slackMessage = new SlackMessage();
                        slackMessage.isFile = false;
                        slackMessage.text = message["text"].ToString();
                    }
                    return slackMessage;
                }
            }

            public async Task<byte[]> getFileContents(string filePath)
            {
                AzureKeyVaultClient _client = new AzureKeyVaultClient();
                var _slacktoken = await _client.GetSecret("SlackToken");

                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _slacktoken);
                    var response = await client.GetAsync(new Uri(filePath));
                    var content = await response.Content.ReadAsByteArrayAsync();

                    return content;
                }
            }
        }

        public class SBQueueClient
        {
            string queueKey = ConfigurationManager.AppSettings["QueueConnectionStringKeyName"];
            AzureKeyVaultClient _client = new AzureKeyVaultClient();

            public async Task EnQueueMessage(SlackMessage message)
            {
                try
                {                           
                    var connectionString = await _client.GetSecret(queueKey);                    
                    QueueClient qClient = QueueClient.CreateFromConnectionString(connectionString);

                    var stringMsg = JsonConvert.SerializeObject(message);
                    var brMsg = new BrokeredMessage(stringMsg);
                    await qClient.SendAsync(brMsg);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Exception {e.Message}");
                }
            }

            public async Task<SlackMessage> DeQueueMessage()
            {
                try
                {
                    var connectionString = await _client.GetSecret(queueKey);
                    
                    QueueClient qClient = QueueClient.CreateFromConnectionString(connectionString);
                    BrokeredMessage message = await qClient.ReceiveAsync();
                    if (message != null)
                    {
                        try
                        {
                            var stringMsg = message.GetBody<string>();
                            var msg = JsonConvert.DeserializeObject<SlackMessage>(stringMsg);
                            await message.CompleteAsync();
                            return msg;
                        }
                        catch (Exception)
                        {
                            // Indicate a problem, unlock message in queue
                            message.Abandon();

                        }
                    }
                    return null;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Exception {e.Message}");
                    return null;
                }
            }
        }

        public class SlackMessage
        {
            public string text { get; set; }
            public string filePath { get; set; }
            public bool isFile { get; set; }
        }

        public class BlobStorageClient
        {
            public async Task<string> UploadDocument(byte[] document, string fileName)
            {
                try
                {                    
                    var containerName = ConfigurationManager.AppSettings["containerName"];
                    var blobEndpoint = ConfigurationManager.AppSettings["blobEndpoint"];
                    var blobSecurityToken = ConfigurationManager.AppSettings["blobSecurityToken"];

                    AzureKeyVaultClient _client = new AzureKeyVaultClient();
                    var token = await _client.GetSecret(blobSecurityToken);
                    var creds = new StorageCredentials(token);
                    var accountWithSas = new CloudStorageAccount(creds, new Uri(blobEndpoint), null, null, null);
                    var blobClientWithSas = accountWithSas.CreateCloudBlobClient();
                    
                    var container = blobClientWithSas.GetContainerReference(containerName);
                    container.CreateIfNotExists();

                    var blob = container.GetBlockBlobReference(fileName);
                    blob.Properties.ContentType = @"image\jpeg";
                    await blob.UploadFromByteArrayAsync(document, 0, document.Length);

                    return string.Format("{0}/{1}", containerName, fileName);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

        public class AzureKeyVaultClient
        {
            private static async Task<string> GetAzureKeyVaultAccessToken(string authority, string resource, string scope)
            {
                var authContext = new AuthenticationContext(authority);
                ClientCredential clientCred = new ClientCredential(
                    ConfigurationManager.AppSettings["AADClientId"],
                    ConfigurationManager.AppSettings["AADClientSecret"]);

                AuthenticationResult result = await authContext.AcquireTokenAsync(resource, clientCred);

                if (result == null)
                {
                    return null;
                }

                return result.AccessToken;
            }


            public async Task<string> GetSecret(string key)
            {
                string url = "https://abootkakv.vault.azure.net/secrets/";
                url = url + key;
                // Create KeyVaultClient with vault credentials
                var kv = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(GetAzureKeyVaultAccessToken));

                // Get a SAS token for our storage from Key Vault
                var sasToken = await kv.GetSecretAsync(url);
                return sasToken.Value;                
            }

        }
    }
}
