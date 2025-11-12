using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ConvertApiDotNet.Model
{
    public class ConvertApiResponse
    {
        public int ConversionCost { get; set; }
        [JsonProperty(PropertyName = "Files")]
        public ConvertApiFile[] Files { get; set; }

        /// <summary>
        /// List of input files uploaded for this specific conversion request.
        /// Not part of the API response payload; populated by the SDK at runtime.
        /// </summary>
        [JsonIgnore]
        public List<ConvertApiFile> UploadedInputFiles { get; internal set; }
    }
}
