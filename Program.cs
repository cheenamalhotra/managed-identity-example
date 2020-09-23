using System;
using System.Threading;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MSITokenTest
{
    class Program
    {
        static void Main(string[] args)
        {
            using (SqlConnection conn = new SqlConnection(args[0]))
            {
                conn.AccessToken = new MockManagedIdentityTokenProvider().AcquireTokenAsync(null).ConfigureAwait(false).GetAwaiter().GetResult();
                conn.Open();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT @@VERSION";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            Console.WriteLine(reader.GetValue(0));
                        }
                    }
                }
                Console.WriteLine("Test completed successfully!");
            }
        }
    }

    #region Mock Managed Identity Token Provider
    internal class MockManagedIdentityTokenProvider
    {
        // HttpClient is intended to be instantiated once and re-used throughout the life of an application.
#if NETFRAMEWORK
        private static readonly HttpClient s_defaultHttpClient = new HttpClient();
#else
        private static readonly HttpClient s_defaultHttpClient = new HttpClient(new HttpClientHandler() { CheckCertificateRevocationList = true });
#endif

        private const string AzureVmImdsApiVersion = "&api-version=2018-02-01";
        private const string AccessToken = "access_token";
        private const string Resource = "https://database.windows.net/";


        private const int DefaultRetryTimeout = 0;
        private const int DefaultMaxRetryCount = 5;

        // Azure Instance Metadata Service (IMDS) endpoint
        private const string AzureVmImdsEndpoint = "http://169.254.169.254/metadata/identity/oauth2/token";

        // Timeout for Azure IMDS probe request
        internal const int AzureVmImdsProbeTimeoutInSeconds = 2;
        internal readonly TimeSpan _azureVmImdsProbeTimeout = TimeSpan.FromSeconds(AzureVmImdsProbeTimeoutInSeconds);

        // Configurable timeout for MSI retry logic
        internal readonly int _retryTimeoutInSeconds = DefaultRetryTimeout;
        internal readonly int _maxRetryCount = DefaultMaxRetryCount;

        public async Task<string> AcquireTokenAsync(string objectId = null)
        {
            // Use the httpClient specified in the constructor. If it was not specified in the constructor, use the default httpClient.
            HttpClient httpClient = s_defaultHttpClient;

            try
            {
                // If user assigned managed identity is specified, include object ID parameter in request
                string objectIdParameter = objectId != default
                    ? $"&object_id={objectId}"
                    : string.Empty;

                // Craft request as per the MSI protocol
                var requestUrl = $"{AzureVmImdsEndpoint}?resource={Resource}{objectIdParameter}{AzureVmImdsApiVersion}";

                HttpResponseMessage response = null;

                try
                {
                    response = await httpClient.SendAsyncWithRetry(getRequestMessage, _retryTimeoutInSeconds, _maxRetryCount, default).ConfigureAwait(false);
                    HttpRequestMessage getRequestMessage()
                    {
                        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                        request.Headers.Add("Metadata", "true");
                        return request;
                    }
                }
                catch (HttpRequestException)
                {
                    // Not throwing exception if Access Token cannot be fetched. Tests will be disabled.
                    return null;
                }

                // If the response is successful, it should have JSON response with an access_token field
                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    int accessTokenStartIndex = jsonResponse.IndexOf(AccessToken) + AccessToken.Length + 3;
                    return jsonResponse.Substring(accessTokenStartIndex, jsonResponse.IndexOf('"', accessTokenStartIndex) - accessTokenStartIndex);
                }

                // RetryFailure : Failed after 5 retries.
                // NonRetryableError : Received a non-retryable error.
                string errorStatusDetail = response.IsRetryableStatusCode()
                    ? "Failed after 5 retries"
                    : "Received a non-retryable error.";

                string errorText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // Not throwing exception if Access Token cannot be fetched. Tests will be disabled.
                return null;
            }
            catch (Exception)
            {
                // Not throwing exception if Access Token cannot be fetched. Tests will be disabled.
                return null;
            }
        }
    }

    #region IMDS Retry Helper
    internal static class SqlManagedIdentityRetryHelper
    {
        internal const int DeltaBackOffInSeconds = 2;
        internal const string RetryTimeoutError = "Reached retry timeout limit set by MsiRetryTimeout parameter in connection string.";

        // for unit test purposes
        internal static bool s_waitBeforeRetry = true;

        internal static bool IsRetryableStatusCode(this HttpResponseMessage response)
        {
            // 404 NotFound, 429 TooManyRequests, and 5XX server error status codes are retryable
            return Regex.IsMatch(((int)response.StatusCode).ToString(), @"404|429|5\d{2}");
        }

        /// <summary>
        /// Implements recommended retry guidance here: https://docs.microsoft.com/en-us/azure/active-directory/managed-identities-azure-resources/how-to-use-vm-token#retry-guidance
        /// </summary>
        internal static async Task<HttpResponseMessage> SendAsyncWithRetry(this HttpClient httpClient, Func<HttpRequestMessage> getRequest, int retryTimeoutInSeconds, int maxRetryCount, CancellationToken cancellationToken)
        {
            using (var timeoutTokenSource = new CancellationTokenSource())
            using (var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutTokenSource.Token, cancellationToken))
            {
                try
                {
                    // if retry timeout is configured, configure cancellation after timeout period elapses
                    if (retryTimeoutInSeconds > 0)
                    {
                        timeoutTokenSource.CancelAfter(TimeSpan.FromSeconds(retryTimeoutInSeconds));
                    }

                    var attempts = 0;
                    var backoffTimeInSecs = 0;
                    HttpResponseMessage response;

                    while (true)
                    {
                        attempts++;

                        try
                        {
                            response = await httpClient.SendAsync(getRequest(), linkedTokenSource.Token).ConfigureAwait(false);

                            if (response.IsSuccessStatusCode || !response.IsRetryableStatusCode() || attempts == maxRetryCount)
                            {
                                break;
                            }
                        }
                        catch (HttpRequestException)
                        {
                            if (attempts == maxRetryCount)
                            {
                                throw;
                            }
                        }

                        if (s_waitBeforeRetry)
                        {
                            // use recommended exponential backoff strategy, and use linked token wait handle so caller or retry timeout is still able to cancel
                            backoffTimeInSecs += (int)Math.Pow(DeltaBackOffInSeconds, attempts);
                            linkedTokenSource.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(backoffTimeInSecs));
                            linkedTokenSource.Token.ThrowIfCancellationRequested();
                        }
                    }

                    return response;
                }
                catch (OperationCanceledException)
                {
                    if (timeoutTokenSource.IsCancellationRequested)
                    {
                        throw new TimeoutException(RetryTimeoutError);
                    }

                    throw;
                }
            }
        }
    }
    #endregion
    #endregion

}
