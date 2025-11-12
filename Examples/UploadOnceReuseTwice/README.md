UploadOnceReuseTwice

This example demonstrates how to upload a file once and reuse it for two conversions using ConvertAPI .NET SDK.

Input
- Local DOCX: Examples/TestFiles/test.docx

Conversions (single upload reused)
- DOCX to PDF
- DOCX to PNG

How it works
- Program.cs explicitly uploads once by calling `GetValueAsync()` on `new ConvertApiFileParam(sourceFile)` to obtain a `FileId`.
- The same `FileId` is passed to `ConvertAsync` twice via `new ConvertApiParam("FileId", uploaded.FileId)`, so the file is not re-uploaded.

Run steps
1) Open ConvertApi.sln.
2) In Program.cs, set your API token instead of "api_token".
3) Set startup project to UploadOnceReuseTwice and run.
4) Output files are saved to your system temp folder; paths are printed to console.

Related docs
- https://www.convertapi.com/docx-to-pdf
- https://www.convertapi.com/docx-to-png