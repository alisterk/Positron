﻿using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CefSharp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Positron.Server.Hosting;
using Cookie = CefSharp.Cookie;

namespace Positron.UI.Internal
{
    internal class PositronResourceHandler : IResourceHandler
    {
        private readonly IWebHost _webHost;
        private readonly ILogger<PositronResourceHandler> _logger;
        private Uri _requestUri;
        private IHttpResponseFeature _response;

        public PositronResourceHandler(IWebHost webHost, ILogger<PositronResourceHandler> logger)
        {
            if (webHost == null)
            {
                throw new ArgumentNullException(nameof(webHost));
            }
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _webHost = webHost;
            _logger = logger;
        }

        public void Dispose()
        {
        }

        public bool ProcessRequest(IRequest request, ICallback callback)
        {
            try
            {
                // Request start/finish is logged already by Asp.Net Core, so we'll only log at debug level
                _logger.LogDebug(LoggerEventIds.RequestStarting, "Request starting {0} '{1}'", request.Method, request.Url);

                var url = new Uri(request.Url);
                _requestUri = url;

                var internalRequest = new PositronRequest
                {
                    Protocol = "HTTP/1.1",
                    Method = request.Method,
                    Path = Uri.UnescapeDataString(url.AbsolutePath),
                    QueryString = url.Query,
                    Scheme = url.Scheme,
                    Headers = new CefHeaderDictionary(request.Headers)
                };

                if (!internalRequest.Headers.ContainsKey("Host"))
                {
                    // We need to pass the Host header to ensure correct URL in logging
                    internalRequest.Headers.Add("Host", new StringValues("positron"));
                }

                if (request.PostData != null && request.PostData.Elements.Any())
                {
                    internalRequest.Body = new MemoryStream(request.PostData.Elements.First().Bytes);
                }

                Task.Run(() =>
                {
                    var processor = _webHost.ServerFeatures.Get<IInternalHttpRequestFeature>();

                    // Do this in a task to ensure it doesn't execute synchronously on the ProcessRequest thread
                    processor.ProcessRequestAsync(internalRequest)
                        .ContinueWith(task =>
                        {
                            _logger.LogDebug(LoggerEventIds.RequestFinished, "Request finished {0} '{1}': {2}",
                                internalRequest.Method, _requestUri, task.Result.StatusCode);

                            using (callback)
                            {
                                _response = task.Result;

                                callback.Continue();
                            }
                        }, TaskContinuationOptions.OnlyOnRanToCompletion)
                        .ContinueWith(task =>
                        {
                            _logger.LogError(LoggerEventIds.RequestError, task.Exception, "Error processing request '{0}'",
                                _requestUri);

                            using (callback)
                            {
                                _response = null;

                                callback.Cancel();
                            }
                        }, TaskContinuationOptions.NotOnRanToCompletion)
                        .ContinueWith(task =>
                        {
                            internalRequest.Body?.Dispose();
                        });
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(LoggerEventIds.RequestError, ex, "Error processing request '{0}'", _requestUri);

                callback.Cancel();
                callback.Dispose();
                return true;
            }
        }

        public void GetResponseHeaders(IResponse response, out long responseLength, out string redirectUrl)
        {
            redirectUrl = null;
            responseLength = (_response.Body?.Length).GetValueOrDefault(-1);

            var mimeType = _response.Headers["Content-Type"].FirstOrDefault();
            if (mimeType != null)
            {
                var i = mimeType.IndexOf(';');
                if (i >= 0)
                {
                    mimeType = mimeType.Substring(0, i);
                }
            }

            response.StatusCode = _response.StatusCode;
            response.StatusText = _response.ReasonPhrase;
            response.MimeType = mimeType;

            var headers = new NameValueCollection();

            foreach (var header in _response.Headers)
            {
                headers[header.Key] = header.Value;
            }

            response.ResponseHeaders = headers;

            if ((response.StatusCode == (int)HttpStatusCode.Redirect) ||
                (response.StatusCode == (int)HttpStatusCode.TemporaryRedirect))
            {
                var redirectLocation = _response.Headers["Location"].FirstOrDefault();
                if (redirectLocation != null)
                {
                    try
                    {
                        var redirectLocationUri = new Uri(redirectLocation, UriKind.RelativeOrAbsolute);

                        if (!redirectLocationUri.IsAbsoluteUri)
                        {
                            redirectLocationUri = new Uri(_requestUri, redirectLocationUri);
                        }

                        redirectUrl = redirectLocationUri.ToString();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(LoggerEventIds.BadRedirectUrlFormat, ex, "Bad redirect url format '{0}'", redirectLocation);
                        // Bad url, ignore
                    }
                }
            }
        }

        public bool ReadResponse(Stream dataOut, out int bytesRead, ICallback callback)
        {
            callback.Dispose();

            if (_response.Body == null)
            {
                bytesRead = 0;
                return false;
            }

            var buffer = new byte[dataOut.Length];
            bytesRead = _response.Body.Read(buffer, 0, buffer.Length);

            dataOut.Write(buffer, 0, bytesRead);

            return bytesRead > 0;
        }

        public bool CanGetCookie(Cookie cookie)
        {
            return true;
        }

        public bool CanSetCookie(Cookie cookie)
        {
            return true;
        }

        public void Cancel()
        {
        }
    }
}
