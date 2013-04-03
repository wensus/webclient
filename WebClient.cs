﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Resources;
using Wensus.Delay;

namespace Wensus
{
    public class WebClient : System.Net.WebClient
    {
        private const string webErrorAction = "weberror";
        private const string webPerformanceAction = "webperf";
        private Stopwatch timer;
        private string requestUrl;

        /// <summary>
        /// Initializes a new instance of the WebClient class.
        /// </summary>
        [SecuritySafeCritical] // Avoids TypeLoadException
        public WebClient()
        {
        }

        /// <summary>
        /// Returns a WebRequest object for the specified resource.
        /// </summary>
        /// <param name="address">A Uri that identifies the resource to request.</param>
        /// <returns>A new WebRequest object for the specified resource.</returns>
        protected override WebRequest GetWebRequest(Uri address)
        {
            this.requestUrl = address.ToString();
         
            var request = base.GetWebRequest(address);
            var httpWebRequest = request as HttpWebRequest;
            if (null != httpWebRequest)
            {
                GzipExtensions.AddAcceptEncodingHeader(httpWebRequest);
            }

            timer = new Stopwatch();
            timer.Start();

            return request;
        }

        /// <summary>
        /// Returns the WebResponse for the specified WebRequest using the specified IAsyncResult.
        /// </summary>
        /// <param name="request">A WebRequest that is used to obtain the response.</param>
        /// <param name="result">An IAsyncResult object obtained from a previous call to BeginGetResponse .</param>
        /// <returns>A WebResponse containing the response for the specified WebRequest.</returns>
        protected override WebResponse GetWebResponse(WebRequest request, IAsyncResult result)
        {
            try
            {
                return new WebResponseWrapper(base.GetWebResponse(request, result));
            }
            catch (WebException ex)
            {
                // Report error
                var response = ex.Response as HttpWebResponse;

                var msg = string.Format("{0}||{1}", requestUrl, response == null ? "000" : ((int) response.StatusCode).ToString());

                Client.Mark(webErrorAction, msg);

                throw;
            }
        }

        protected override void OnDownloadProgressChanged(DownloadProgressChangedEventArgs e)
        {
            if (e.ProgressPercentage == 100)
            {
                // Report data amount and time
                timer.Stop();

                var elapsedTime = timer.ElapsedMilliseconds;
                var dataTransferred = e.BytesReceived;

                var msg = string.Format("{0}||{1}||{2}", requestUrl, elapsedTime, dataTransferred);

                Client.Mark(webPerformanceAction, msg);
            }

            base.OnDownloadProgressChanged(e);
        }

        /// <summary>
        /// Class that wraps WebResponse to return an uncompressed response stream when GZIP was used.
        /// </summary>
        private class WebResponseWrapper : WebResponse
        {
            /// <summary>
            /// Stores the wrapped WebResponse.
            /// </summary>
            private readonly WebResponse _response;


            /// <summary>
            /// Initializes a new instance of the WebResponseWrapper class.
            /// </summary>
            /// <param name="response">WebResponse to wrap.</param>
            public WebResponseWrapper(WebResponse response)
            {
                _response = response;
            }

            /// <summary>
            /// Returns the data stream from the Internet resource.
            /// </summary>
            /// <returns>An instance of the Stream class for reading data from the Internet resource.</returns>
            public override Stream GetResponseStream()
            {
                var httpWebResponse = _response as HttpWebResponse;
                if (null != httpWebResponse)
                {
                    return httpWebResponse.GetCompressedResponseStream();
                }
                else
                {
                    return _response.GetResponseStream();
                }
            }

            // Pass-through wrapper implementations
            public override void Close()
            {
                _response.Close();
            }
            public override long ContentLength
            {
                get { return _response.ContentLength; }
            }
            public override string ContentType
            {
                get { return _response.ContentType; }
            }
            public override WebHeaderCollection Headers
            {
                get { return _response.Headers; }
            }
            public override Uri ResponseUri
            {
                get { return _response.ResponseUri; }
            }
            public override bool SupportsHeaders
            {
                get { return _response.SupportsHeaders; }
            }
        }
    }


    namespace Delay
    {
        /// <summary>
        /// Class that provides helper methods to add support for GZIP to Windows Phone.
        /// </summary>
        /// <remarks>
        /// GZIP file format specification: http://tools.ietf.org/rfc/rfc1952.txt
        /// ZIP file specification: http://www.pkware.com/documents/casestudies/APPNOTE.TXT
        /// </remarks>
        internal static class GzipExtensions
        {
            /// <summary>
            /// HTTP request header Accept-Encoding string.
            /// </summary>
            private static string GZIP = "gzip";

            /// <summary>
            /// Adds an HTTP Accept-Encoding header for GZIP.
            /// </summary>
            /// <param name="request">Request to modify.</param>
            public static void AddAcceptEncodingHeader(HttpWebRequest request)
            {
                if (null == request)
                {
                    throw new ArgumentNullException("request");
                }
                request.Headers[HttpRequestHeader.AcceptEncoding] = GZIP;
            }

            /// <summary>
            /// Begins an asynchronous request to an Internet resource, using GZIP when supported by the server.
            /// </summary>
            /// <param name="request">Request to act on.</param>
            /// <param name="callback">The AsyncCallback delegate.</param>
            /// <param name="state">The state object for this request.</param>
            /// <returns>An IAsyncResult that references the asynchronous request for a response.</returns>
            /// <remarks>
            /// Functionally equivalent to BeginGetResponse (with GZIP).
            /// </remarks>
            public static IAsyncResult BeginGetCompressedResponse(this HttpWebRequest request, AsyncCallback callback, object state)
            {
                AddAcceptEncodingHeader(request);
                return request.BeginGetResponse(callback, state);
            }

            /// <summary>
            /// Returns the data stream from the Internet resource.
            /// </summary>
            /// <param name="response">Response to act on.</param>
            /// <returns>An instance of the Stream class for reading data from the Internet resource.</returns>
            /// Functionally equivalent to GetResponseStream (with GZIP).
            [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Returning a Stream for the caller to use.")]
            public static Stream GetCompressedResponseStream(this HttpWebResponse response)
            {
                // Validate arguments
                if (null == response)
                {
                    throw new ArgumentNullException("response");
                }

                // Check the response for GZIP
                var responseStream = response.GetResponseStream();
                if (string.Equals(response.Headers[HttpRequestHeader.ContentEncoding], GZIP, StringComparison.OrdinalIgnoreCase))
                {
                    // Read header
                    if ((0x1f != responseStream.ReadByte()) || // ID1
                        (0x8b != responseStream.ReadByte()) || // ID2
                        (8 != responseStream.ReadByte()))    // CM (8 == deflate)
                    {
                        throw new NotSupportedException("Compressed data not in the expected format.");
                    }

                    // Read flags
                    var flg = responseStream.ReadByte(); // FLG
                    var fhcrc = 0 != (0x2 & flg); // CRC16 present before compressed data
                    var fextra = 0 != (0x4 & flg); // extra fields present
                    var fname = 0 != (0x8 & flg); // original file name present
                    var fcomment = 0 != (0x10 & flg); // file comment present

                    // Skip unsupported fields
                    responseStream.ReadByte(); responseStream.ReadByte(); responseStream.ReadByte(); responseStream.ReadByte(); // MTIME
                    responseStream.ReadByte(); // XFL
                    responseStream.ReadByte(); // OS
                    if (fextra)
                    {
                        // Skip XLEN bytes of data
                        var xlen = responseStream.ReadByte() | (responseStream.ReadByte() << 8);
                        while (0 < xlen)
                        {
                            responseStream.ReadByte();
                            xlen--;
                        }
                    }
                    if (fname)
                    {
                        // Skip 0-terminated file name
                        while (0 != responseStream.ReadByte())
                        {
                        }
                    }
                    if (fcomment)
                    {
                        // Skip 0-terminated file comment
                        while (0 != responseStream.ReadByte())
                        {
                        }
                    }
                    if (fhcrc)
                    {
                        responseStream.ReadByte(); responseStream.ReadByte(); // CRC16
                    }

                    // Read compressed data
                    const int zipHeaderSize = 30 + 1; // 30 bytes + 1 character for file name
                    const int zipFooterSize = 68 + 1; // 68 bytes + 1 character for file name

                    // Download unknown amount of compressed data efficiently (note: Content-Length header is not always reliable)
                    var buffers = new List<byte[]>();
                    var buffer = new byte[4096];
                    var bytesInBuffer = 0;
                    var totalBytes = 0;
                    var bytesRead = 0;
                    do
                    {
                        if (buffer.Length == bytesInBuffer)
                        {
                            // Full, allocate another
                            buffers.Add(buffer);
                            buffer = new byte[buffer.Length];
                            bytesInBuffer = 0;
                        }
                        Debug.Assert(bytesInBuffer < buffer.Length);
                        bytesRead = responseStream.Read(buffer, bytesInBuffer, buffer.Length - bytesInBuffer);
                        bytesInBuffer += bytesRead;
                        totalBytes += bytesRead;
                    } while (0 < bytesRead);
                    buffers.Add(buffer);

                    // "Trim" crc32 and isize fields off the end
                    var compressedSize = totalBytes - 4 - 4;
                    if (compressedSize < 0)
                    {
                        throw new NotSupportedException("Compressed data not in the expected format.");
                    }

                    // Create contiguous buffer
                    var compressedBytes = new byte[zipHeaderSize + compressedSize + zipFooterSize];
                    var offset = zipHeaderSize;
                    var remainingBytes = totalBytes;
                    foreach (var b in buffers)
                    {
                        var length = Math.Min(b.Length, remainingBytes);
                        Array.Copy(b, 0, compressedBytes, offset, length);
                        offset += length;
                        remainingBytes -= length;
                    }
                    Debug.Assert(0 == remainingBytes);

                    // Read footer from end of compressed bytes (note: footer is within zipFooterSize; will be overwritten below)
                    Debug.Assert(totalBytes <= compressedSize + zipFooterSize);
                    offset = zipHeaderSize + compressedSize;
                    var crc32 = compressedBytes[offset + 0] | (compressedBytes[offset + 1] << 8) | (compressedBytes[offset + 2] << 16) | (compressedBytes[offset + 3] << 24);
                    var isize = compressedBytes[offset + 4] | (compressedBytes[offset + 5] << 8) | (compressedBytes[offset + 6] << 16) | (compressedBytes[offset + 7] << 24);

                    // Create ZIP file stream
                    const string fileName = "f"; // MUST be 1 character (offsets below assume this)
                    Debug.Assert(1 == fileName.Length);
                    var zipFileMemoryStream = new MemoryStream(compressedBytes);
                    var writer = new BinaryWriter(zipFileMemoryStream);

                    // Local file header
                    writer.Write((uint)0x04034b50); // local file header signature
                    writer.Write((ushort)20); // version needed to extract (2.0 == compressed using deflate)
                    writer.Write((ushort)0); // general purpose bit flag
                    writer.Write((ushort)8); // compression method (8: deflate)
                    writer.Write((ushort)0); // last mod file time
                    writer.Write((ushort)0); // last mod file date
                    writer.Write(crc32); // crc-32
                    writer.Write(compressedSize); // compressed size
                    writer.Write(isize); // uncompressed size
                    writer.Write((ushort)1); // file name length
                    writer.Write((ushort)0); // extra field length
                    writer.Write((byte)fileName[0]); // file name

                    // File data (already present)
                    zipFileMemoryStream.Seek(compressedSize, SeekOrigin.Current);

                    // Central directory structure
                    writer.Write((uint)0x02014b50); // central file header signature
                    writer.Write((ushort)20); // version made by
                    writer.Write((ushort)20); // version needed to extract (2.0 == compressed using deflate)
                    writer.Write((ushort)0); // general purpose bit flag
                    writer.Write((ushort)8); // compression method
                    writer.Write((ushort)0); // last mod file time
                    writer.Write((ushort)0); // last mod file date
                    writer.Write(crc32); // crc-32
                    writer.Write(compressedSize); // compressed size
                    writer.Write(isize); // uncompressed size
                    writer.Write((ushort)1); // file name length
                    writer.Write((ushort)0); // extra field length
                    writer.Write((ushort)0); // file comment length
                    writer.Write((ushort)0); // disk number start
                    writer.Write((ushort)0); // internal file attributes
                    writer.Write((uint)0); // external file attributes
                    writer.Write((uint)0); // relative offset of local header
                    writer.Write((byte)fileName[0]); // file name
                    // End of central directory record
                    writer.Write((uint)0x06054b50); // end of central dir signature
                    writer.Write((ushort)0); // number of this disk
                    writer.Write((ushort)0); // number of the disk with the start of the central directory
                    writer.Write((ushort)1); // total number of entries in the central directory on this disk
                    writer.Write((ushort)1); // total number of entries in the central directory
                    writer.Write((uint)(46 + 1)); // size of the central directory (46 bytes + 1 character for file name)
                    writer.Write((uint)(zipHeaderSize + compressedSize)); // offset of start of central directory with respect to the starting disk number
                    writer.Write((ushort)0); // .ZIP file comment length

                    // Reset ZIP file stream to beginning
                    zipFileMemoryStream.Seek(0, SeekOrigin.Begin);

                    // Return the decompressed stream
                    return Application.GetResourceStream(
                        new StreamResourceInfo(zipFileMemoryStream, null),
                        new Uri(fileName, UriKind.Relative))
                        .Stream;
                }
                else
                {
                    // Not GZIP-compressed; return stream as-is
                    return responseStream;
                }
            }
        }
    }

}
