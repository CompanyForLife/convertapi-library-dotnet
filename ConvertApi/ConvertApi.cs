using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ConvertApiDotNet.Constants;
using ConvertApiDotNet.Exceptions;
using ConvertApiDotNet.Interface;
using ConvertApiDotNet.Model;
using Newtonsoft.Json;
using Microsoft.OpenApi;

namespace ConvertApiDotNet
{
    public class ConvertApi
    {
        public static string ApiToken;
        public static string ApiBaseUri = "https://v2.convertapi.com";
        private static IConvertApiHttpClient _convertApiHttpClient;

        public ConvertApi()
        {
        }

        /// <summary>
        /// Initializes a new instance of the ConvertApi class.
        /// </summary>
        /// <param name="apiToken">The authentication credentials token for ConvertApi. Can be obtained from https://www.convertapi.com/a/authentication</param>
        /// <param name="convertApiHttpClient">The HTTP client for making API requests.</param>
        public ConvertApi(string apiToken, IConvertApiHttpClient convertApiHttpClient)
        {
            ApiToken = apiToken;
            _convertApiHttpClient = convertApiHttpClient;
        }

        /// <summary>
        /// Initializes a new instance of the ConvertApi class.
        /// </summary>
        /// <param name="apiToken">The authentication credentials token for ConvertApi. Can be obtained from https://www.convertapi.com/a/authentication</param>
        public ConvertApi(string apiToken)
        {
            if (string.IsNullOrEmpty(apiToken))
                throw new ArgumentNullException(nameof(apiToken));

            ApiToken = apiToken;
        }

        public static IConvertApiHttpClient GetClient()
        {
            return _convertApiHttpClient ?? (_convertApiHttpClient = new DefaultConvertApiHttpClient());
        }

        public async Task<ConvertApiResponse> ConvertAsync(string fromFormat, string toFormat, params ConvertApiBaseParam[] parameters)
        {
            return await ConvertAsync(fromFormat, toFormat, (IEnumerable<ConvertApiBaseParam>)parameters);
        }

        public async Task<ConvertApiResponse> ConvertAsync(string fromFormat, string toFormat, IEnumerable<ConvertApiBaseParam> parameters)
        {
            var content = new MultipartFormDataContent
            {
                { new StringContent("true"), "StoreFile" }
            };

            var ignoredParameters = new[] { "StoreFile", "Async", "JobId" };

            var validParameters = parameters
                .Where(n => !ignoredParameters.Contains(n.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var dicList = new ParamDictionary();
            var uploadedInputs = new List<ConvertApiFile>();

            foreach (var parameter in validParameters)
            {
                if (parameter is ConvertApiParam valueParam)
                {
                    foreach (var value in valueParam.GetValues())
                    {
                        dicList.Add(parameter.Name, value);
                    }
                }
                else if (parameter is ConvertApiFileParam fileParam)
                {
                    var convertApiUpload = await fileParam.GetUploadedFileAsync();
                    if (convertApiUpload != null)
                    {
                        dicList.Add(parameter.Name, convertApiUpload);
                        uploadedInputs.Add(convertApiUpload);
                    }
                    else
                    {
                        foreach (var value in fileParam.GetValues())
                        {
                            dicList.Add(parameter.Name, value);

                            // Track server-stored files referenced by URL so they can be deleted later
                            if (Uri.TryCreate(value, UriKind.Absolute, out var refUri))
                            {
                                try
                                {
                                    var apiHost = new Uri(ApiBaseUri).Host;
                                    if (string.Equals(refUri.Host, apiHost, StringComparison.OrdinalIgnoreCase))
                                    {
                                        uploadedInputs.Add(new ConvertApiFile { Url = refUri });
                                    }
                                }
                                catch
                                {
                                    // ignore malformed ApiBaseUri or any unexpected issues
                                }
                            }
                        }
                    }
                }
            }

            foreach (var s in dicList.Get())
            {
                switch (s.Value)
                {
                    case string value:
                        content.Add(new StringContent(value), s.Key);
                        break;

                    case ConvertApiFile upload:
                        content.Add(new StringContent(upload.FileId), s.Key);

                        // Set FROM format if it is not set
                        if (string.Equals(fromFormat.ToLower(), "*", StringComparison.OrdinalIgnoreCase))
                        {
                            fromFormat = upload.FileExt;
                        }

                        break;
                }
            }

            var url = new UriBuilder(ApiBaseUri)
            {
                Path = $"convert/{fromFormat}/to/{toFormat}",
            };

            TimeSpan? requestTimeOut = null;
            var timeoutParameter = dicList.Find("timeout");
            if (!string.IsNullOrEmpty(timeoutParameter) && int.TryParse(timeoutParameter, out var parsedTimeOut))
            {
                requestTimeOut = TimeSpan.FromSeconds(parsedTimeOut)
                    .Add(ConvertApiConstants.ConversionTimeoutDelta);
            }

            var response = await GetClient().PostAsync(url.Uri, requestTimeOut, content, ApiToken);
            var result = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ConvertApiException(
                    response.StatusCode,
                    $"Conversion from {fromFormat} to {toFormat} error. {response.ReasonPhrase}",
                    result);

            var apiResponse = JsonConvert.DeserializeObject<ConvertApiResponse>(result);
            if (apiResponse != null)
            {
                // Attach uploaded input files so DeleteFilesAsync(response) can remove them later
                apiResponse.UploadedInputFiles = uploadedInputs;
            }

            return apiResponse;
        }

        /// <summary>
        /// Retrieves strongly typed metadata about a specific converter by parsing the service OpenAPI schema.
        /// </summary>
        /// <param name="src">Source format, e.g. "docx" or "*".</param>
        /// <param name="dst">Destination format, e.g. "pdf".</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Returns converter metadata model to build dynamic forms/UI.</returns>
        public async Task<Converter> GetConverterInfo(string src, string dst, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(src)) throw new ArgumentNullException(nameof(src));
            if (string.IsNullOrWhiteSpace(dst)) throw new ArgumentNullException(nameof(dst));

            // Endpoint without leading "convert/" as used by the info/openapi endpoint
            var endpoint = $"{src.Trim('/')}/to/{dst.Trim('/')}".Trim('/');

            // Try endpoint-specific OpenAPI first, then fall back to root.
            var doc = await TryFetchOpenApiAsync($"info/openapi/{endpoint}", ct)
                      ?? await TryFetchOpenApiAsync("info/openapi", ct);

            if (doc == null)
                throw new InvalidOperationException("OpenAPI document could not be retrieved.");

            var pathKey = $"/convert/{endpoint}";
            if (!doc.Paths.TryGetValue(pathKey, out var pathItem) || pathItem == null)
                throw new InvalidOperationException($"Converter path '{pathKey}' not found in OpenAPI document.");

            // Find POST operation without depending on OperationType enum
            var postOp = pathItem.Operations?
                .FirstOrDefault(kvp =>
                    kvp.Key != null &&
                    string.Equals(kvp.Key.ToString(), "Post", StringComparison.OrdinalIgnoreCase))
                .Value;

            if (postOp == null)
                throw new InvalidOperationException("POST operation not defined for the converter in OpenAPI.");

            var model = new Converter
            {
                Title = string.IsNullOrWhiteSpace(pathItem.Summary)
                    ? (postOp.Summary ?? pathKey)
                    : pathItem.Summary,
                Summary = string.IsNullOrWhiteSpace(pathItem.Description)
                    ? postOp.Description
                    : pathItem.Description,
                AcceptsFormats = string.Empty,
                AcceptsMultiple = false,
                Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            // Collect accept formats from x-ca-source-formats extension (operation has priority)
            var accept = GetExtensionString(postOp.Extensions, "x-ca-source-formats")
                         ?? GetExtensionString(pathItem.Extensions, "x-ca-source-formats");

            if (!string.IsNullOrWhiteSpace(accept))
            {
                var list = accept
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => (v ?? string.Empty).Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.StartsWith(".") ? v : "." + v)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                model.AcceptsFormats = string.Join(",", list);
            }

            // Parse request body schema for parameters and multiple file support
            if (postOp.RequestBody?.Content != null)
            {
                foreach (var content in postOp.RequestBody.Content)
                {
                    var schema = content.Value?.Schema;
                    if (schema == null) continue;

                    if (schema.Properties != null)
                    {
                        foreach (var kvp in schema.Properties)
                        {
                            var name = kvp.Key;
                            var s = kvp.Value;

                            // Detect files support
                            var isBinary = string.Equals(s.Format, "binary", StringComparison.OrdinalIgnoreCase);
                            var isFilesArray = s.Type == JsonSchemaType.Array
                                               && s.Items != null
                                               && string.Equals(s.Items.Format, "binary", StringComparison.OrdinalIgnoreCase);

                            if (isFilesArray && name.Equals("files", StringComparison.OrdinalIgnoreCase))
                                model.AcceptsMultiple = true;

                            // Skip file/binary parameters from the simple dictionary
                            if (isBinary || isFilesArray)
                                continue;

                            var label = GetExtensionString(s.Extensions, "x-ca-label") ?? name;
                            model.Parameters[name] = label;

                            // If accept list still empty, try property-level extension
                            if (string.IsNullOrWhiteSpace(model.AcceptsFormats))
                            {
                                var propAccept = GetExtensionString(s.Extensions, "x-ca-source-formats");
                                if (!string.IsNullOrWhiteSpace(propAccept))
                                {
                                    var list2 = propAccept
                                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(v => (v ?? string.Empty).Trim())
                                        .Where(v => !string.IsNullOrWhiteSpace(v))
                                        .Select(v => v.StartsWith(".") ? v : "." + v)
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                                        .ToList();

                                    model.AcceptsFormats = string.Join(",", list2);
                                }
                            }
                        }
                    }
                }
            }

            // Fallbacks
            if (string.IsNullOrWhiteSpace(model.AcceptsFormats))
            {
                var from = (src ?? string.Empty).Trim().Trim('.');
                if (!string.IsNullOrWhiteSpace(from) && !string.Equals(from, "*", StringComparison.Ordinal))
                    model.AcceptsFormats = "." + from.ToLowerInvariant();
            }

            return model;
        }

        private async Task<OpenApiDocument> TryFetchOpenApiAsync(string path, System.Threading.CancellationToken ct)
        {
            var url = new UriBuilder(ApiBaseUri)
            {
                Path = path
            };

            try
            {
                var response = await GetClient().GetAsync(url.Uri, ConvertApiConstants.DownloadTimeout, ApiToken);
                if (response.StatusCode != HttpStatusCode.OK)
                    return null;

                var json = await response.Content.ReadAsStringAsync();

                // Use new OpenAPI.NET Parse API (3.x) instead of OpenApiStreamReader
                var readResult = OpenApiDocument.Parse(json);
                return readResult.Document;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Safely read an extension value as a comma-separated string, without depending
        /// on specific OpenAPI.NET extension types or namespaces.
        /// </summary>
        private static string GetExtensionString<T>(IDictionary<string, T> extensions, string key)
        {
            if (extensions == null) return null;
            T ext;
            if (!extensions.TryGetValue(key, out ext) || ext == null) return null;

            // Try a Value property first (OpenApiString-style)
            var valueProp = ext.GetType().GetProperty("Value");
            if (valueProp != null && valueProp.PropertyType == typeof(string))
            {
                return (string)valueProp.GetValue(ext);
            }

            // If it looks like a collection (OpenApiArray-style), join string-ish values
            var enumerable = ext as System.Collections.IEnumerable;
            if (enumerable != null && !(ext is string))
            {
                var items = new List<string>();

                foreach (var item in enumerable)
                {
                    if (item == null) continue;

                    var itemValueProp = item.GetType().GetProperty("Value");
                    string v = null;

                    if (itemValueProp != null && itemValueProp.PropertyType == typeof(string))
                    {
                        v = (string)itemValueProp.GetValue(item);
                    }
                    else
                    {
                        v = item.ToString();
                    }

                    if (!string.IsNullOrWhiteSpace(v))
                        items.Add(v);
                }

                if (items.Count > 0)
                    return string.Join(",", items);
            }

            // Fallback – last resort
            return ext.ToString();
        }

        /// <summary>
        /// Get user/account information
        /// </summary>
        /// <returns>Returns account status like user name, credits left and other information</returns>
        public async Task<ConvertApiUser> GetUserAsync()
        {
            var url = new UriBuilder(ApiBaseUri)
            {
                Path = "user"
            };

            var response = await GetClient().GetAsync(url.Uri, ConvertApiConstants.DownloadTimeout, ApiToken);
            var result = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new ConvertApiException(
                    response.StatusCode,
                    $"Retrieve user information failed. {response.ReasonPhrase}",
                    result);

            return JsonConvert.DeserializeObject<ConvertApiUser>(result);
        }
    }
}
