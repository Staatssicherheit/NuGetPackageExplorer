﻿using System;
using System.Globalization;
using System.IO;
using System.Net;
using NuGet.Resources;

namespace NuGet
{
    public class GalleryServer
    {
        private const string ServiceEndpoint = "/api/v2/package";
        private const string ApiKeyHeader = "X-NuGet-ApiKey";

        private readonly Lazy<Uri> _baseUri;
        private readonly string _source;
        private readonly string _userAgent;

        public GalleryServer(string source, string userAgent)
        {
            if (String.IsNullOrEmpty(source))
            {
                throw new ArgumentException("Argument cannot be null or empty.", "source");
            }
            _source = source;
            _userAgent = userAgent;
            _baseUri = new Lazy<Uri>(ResolveBaseUrl);
        }

        public bool IsV1Protocol
        {
            get
            {
                return false;
            }
        }

        public string Source
        {
            get { return _source; }
        }

        public void PushPackage(string apiKey, Stream packageStream, IPackageMetadata package, bool pushAsUnlisted, IObserver<int> progressObserver)
        {
            HttpClient client = GetClient("", "PUT", "application/octet-stream");

            client.SendingRequest += (sender, e) =>
                {
                    var request = (HttpWebRequest)e.Request;

                    // Set the timeout to the same as the read write timeout (5 mins is the default)
                    request.Timeout = request.ReadWriteTimeout;
                    request.Headers.Add(ApiKeyHeader, apiKey);

                    var multiPartRequest = new MultipartWebRequest();
                    multiPartRequest.AddFile(packageStream, "package");

                    multiPartRequest.CreateMultipartRequest(request);
                };

            bool successful = EnsureSuccessfulResponse(client, progressObserver, HttpStatusCode.Created);
            if (pushAsUnlisted && successful)
            {
                DeletePackage(apiKey, package.Id, package.Version.ToString(), progressObserver);
            }
        }

        public void DeletePackage(string apiKey, string packageId, string packageVersion, IObserver<int> progressObserver)
        {
            // Review: Do these values need to be encoded in any way?
            var url = String.Join("/", packageId, packageVersion);
            HttpClient client = GetClient(url, "DELETE", "text/html");

            client.SendingRequest += (sender, e) =>
            {
                var request = (HttpWebRequest)e.Request;
                request.Headers.Add(ApiKeyHeader, apiKey);
            };
            EnsureSuccessfulResponse(client, progressObserver);
        }

        private HttpClient GetClient(string path, string method, string contentType)
        {
            var baseUrl = _baseUri.Value;
            Uri requestUri = GetServiceEndpointUrl(baseUrl, path);

            var client = new HttpClient(requestUri)
            {
                ContentType = contentType,
                Method = method
            };

            if (!String.IsNullOrEmpty(_userAgent))
            {
                client.UserAgent = _userAgent;
            }

            return client;
        }

        internal static Uri GetServiceEndpointUrl(Uri baseUrl, string path)
        {
            Uri requestUri;
            if (String.IsNullOrEmpty(baseUrl.AbsolutePath.TrimStart('/')))
            {
                // If there's no host portion specified, append the url to the client.
                requestUri = new Uri(baseUrl, ServiceEndpoint + '/' + path);
            }
            else
            {
                requestUri = new Uri(baseUrl, path);
            }
            return requestUri;
        }

        private static bool EnsureSuccessfulResponse(HttpClient client, IObserver<int> progressObserver, HttpStatusCode? expectedStatusCode = null)
        {
            HttpWebResponse response = null;
            try
            {
                progressObserver.OnNext(0);
                response = (HttpWebResponse)client.GetResponse();
                if (response != null &&
                    ((expectedStatusCode.HasValue && expectedStatusCode.Value != response.StatusCode) ||

                    // If expected status code isn't provided, just look for anything 400 (Client Errors) or higher (incl. 500-series, Server Errors)
                    // 100-series is protocol changes, 200-series is success, 300-series is redirect.
                    (!expectedStatusCode.HasValue && (int)response.StatusCode >= 400)))
                {
                    throw new InvalidOperationException(String.Format(CultureInfo.CurrentCulture, NuGetResources.PackageServerError, response.StatusDescription, String.Empty));
                }
                progressObserver.OnCompleted();
                return true;
            }
            catch (WebException e)
            {
                if (e.Response == null)
                {
                    throw;
                }

                response = (HttpWebResponse)e.Response;
                if (expectedStatusCode != response.StatusCode)
                {
                    Exception error = new WebException(
                        String.Format(CultureInfo.CurrentCulture, NuGetResources.PackageServerError, response.StatusDescription, e.Message), e);
                    progressObserver.OnError(error);
                }

                return false;
            }
            finally
            {
                if (response != null)
                {
                    response.Close();
                    response = null;
                }
            }
        }

        private Uri ResolveBaseUrl()
        {
            Uri uri;

            try
            {
                var client = new RedirectedHttpClient(new Uri(Source));
                uri = client.Uri;
            }
            catch (WebException ex)
            {
                var response = (HttpWebResponse)ex.Response;
                if (response == null)
                {
                    throw;
                }

                uri = response.ResponseUri;
            }

            return EnsureTrailingSlash(uri);
        }

        private static Uri EnsureTrailingSlash(Uri uri)
        {
            string value = uri.OriginalString;
            if (!value.EndsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                value += "/";
            }
            return new Uri(value);
        }
    }
}
