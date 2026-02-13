//
// Authors:
//   Lucas Ontivero lucasontivero@gmail.com
//
// Copyright (C) 2014 Lucas Ontivero
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Open.Nat
{
	internal class SoapClient
	{
		private readonly string _serviceType;
		private readonly Uri _url;
		private static readonly HttpClient HttpClient = new HttpClient();

		public SoapClient(Uri url, string serviceType)
		{
			_url = url;
			_serviceType = serviceType;
		}

		public async Task<XmlDocument> InvokeAsync(string operationName, IDictionary<string, object> args)
		{
			NatDiscoverer.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "SOAPACTION: **{0}** url:{1}", operationName,
												 _url);
			string messageBody = BuildMessageBody(operationName, args);

			var request = new HttpRequestMessage(HttpMethod.Post, _url);
			request.Headers.ConnectionClose = true;
			request.Headers.Add("SOAPACTION", "\"" + _serviceType + "#" + operationName + "\"");
			request.Content = new StringContent(messageBody, Encoding.UTF8, "text/xml");

			HttpResponseMessage response;
			try
			{
				response = await HttpClient.SendAsync(request);
			}
			catch (HttpRequestException ex)
			{
				NatDiscoverer.TraceSource.TraceEvent(TraceEventType.Verbose, 0, "HttpRequestException: {0}", ex.Message);
				throw;
			}

			var responseBody = await response.Content.ReadAsStringAsync();
			var responseXml = GetXmlDocument(responseBody);
			return responseXml;
		}

		private string BuildMessageBody(string operationName, IEnumerable<KeyValuePair<string, object>> args)
		{
			var sb = new StringBuilder();
			sb.AppendLine("<s:Envelope ");
			sb.AppendLine("   xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
			sb.AppendLine("   s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
			sb.AppendLine("   <s:Body>");
			sb.AppendLine("	  <u:" + operationName + " xmlns:u=\"" + _serviceType + "\">");
			foreach (var a in args)
			{
				sb.AppendLine("		 <" + a.Key + ">" + Convert.ToString(a.Value, CultureInfo.InvariantCulture) +
							  "</" + a.Key + ">");
			}
			sb.AppendLine("	  </u:" + operationName + ">");
			sb.AppendLine("   </s:Body>");
			sb.Append("</s:Envelope>\r\n\r\n");
			return sb.ToString();
		}

		private XmlDocument GetXmlDocument(string response)
		{
			XmlNode node;
			var doc = new XmlDocument();
			doc.LoadXml(response);

			var nsm = new XmlNamespaceManager(doc.NameTable);

			// Error messages should be found under this namespace
			nsm.AddNamespace("errorNs", "urn:schemas-upnp-org:control-1-0");

			// Check to see if we have a fault code message.
			if ((node = doc.SelectSingleNode("//errorNs:UPnPError", nsm)) != null)
			{
				int code = Convert.ToInt32(node.GetXmlElementText("errorCode"), CultureInfo.InvariantCulture);
				string errorMessage = node.GetXmlElementText("errorDescription");
				NatDiscoverer.TraceSource.LogWarn("Server failed with error: {0} - {1}", code, errorMessage);
				throw new MappingException(code, errorMessage);
			}

			return doc;
		}
	}
}