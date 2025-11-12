using System;

namespace ConvertApiDotNet.Model
{
    public class ConvertApiFile
    {
        public string FileId { get; set; }        
        public string FileName { get; set; }        
        public string FileExt { get; set; }
        public int FileSize { get; set; }
        public Uri Url { get; set; }
    }
}
