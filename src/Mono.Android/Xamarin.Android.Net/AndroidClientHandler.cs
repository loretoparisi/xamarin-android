using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Android.Runtime;
using Java.IO;
using Java.Net;
using Java.Security;
using Java.Security.Cert;
using Javax.Net.Ssl;

namespace Xamarin.Android.Net
{
	/// <summary>
	/// A custom implementation of <see cref="System.Net.Http.HttpClientHandler"/> which internally uses <see cref="Java.Net.HttpURLConnection"/>
	/// (or its HTTPS incarnation) to send HTTP requests.
	/// </summary>
	/// <remarks>
	/// <para>Instance of this class is used to configure <see cref="System.Net.Http.HttpClient"/> instance
	/// in the following way:
	/// 
	/// <example>
	/// var handler = new AndroidClientHandler {
	///    UseCookies = true,
	///    AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip,
	/// };
	///
	/// var httpClient = new HttpClient (handler);
	/// var response = httpClient.GetAsync ("http://example.com")?.Result as AndroidHttpResponseMessage;
	/// </example></para>
	/// <para>
	/// The class supports pre-authentication of requests albeit in a slightly "manual" way. Namely, whenever a request to a server requiring authentication
	/// is made and no authentication credentials are provided in the <see cref="PreAuthenticationData"/> property (which is usually the case on the first
	/// request), the <see cref="RequestNeedsAuthorization"/> property will return <c>true</c> and the <see cref="RequestedAuthentication"/> property will
	/// contain all the authentication information gathered from the server. The application must then fill in the blanks (i.e. the credentials) and re-send
	/// the request configured to perform pre-authentication. The reason for this manual process is that the underlying Java HTTP client API supports only a 
	/// single, VM-wide, authentication handler which cannot be configured to handle credentials for several requests. AndroidClientHandler, therefore, implements
	/// the authentication in managed .NET code. Message handler supports both Basic and Digest authentication. If an authentication scheme that's not supported
	/// by AndroidClientHandler is requested by the server, the application can provide its own authentication module (<see cref="AuthenticationData"/>, 
	/// <see cref="PreAuthenticationData"/>) to handle the protocol authorization.</para>
	/// <para>AndroidClientHandler also supports requests to servers with "invalid" (e.g. self-signed) SSL certificates. Since this process is a bit convoluted using
	/// the Java APIs, AndroidClientHandler defines two ways to handle the situation. First, easier, is to store the necessary certificates (either CA or server certificates)
	/// in the <see cref="TrustedCerts"/> collection or, after deriving a custom class from AndroidClientHandler, by overriding one or more methods provided for this purpose
	/// (<see cref="ConfigureTrustManagerFactory"/>, <see cref="ConfigureKeyManagerFactory"/> and <see cref="ConfigureKeyStore"/>). The former method should be sufficient
	/// for most use cases, the latter allows the application to provide fully customized key store, trust manager and key manager, if needed. Note that the instance of
	/// AndroidClientHandler configured to accept an "invalid" certificate from the particular server will most likely fail to validate certificates from other servers (even
	/// if they use a certificate with a fully validated trust chain) unless you store the CA certificates from your Android system in <see cref="TrustedCerts"/> along with
	/// the self-signed certificate(s).</para>
	/// </remarks>
	public class AndroidClientHandler : HttpClientHandler
	{
		internal const string LOG_APP = "monodroid-net";

		const string GZIP_ENCODING = "gzip";
		const string DEFLATE_ENCODING = "deflate";
		const string IDENTITY_ENCODING = "identity";

		static readonly HashSet <string> known_content_headers = new HashSet <string> (StringComparer.OrdinalIgnoreCase) {
			"Allow",
			"Content-Disposition",
			"Content-Encoding",
			"Content-Language",
			"Content-Length",
			"Content-Location",
			"Content-MD5",
			"Content-Range",
			"Content-Type",
			"Expires",
			"Last-Modified"
		};

		static readonly List <IAndroidAuthenticationModule> authModules = new List <IAndroidAuthenticationModule> {
			new AuthModuleBasic (),
			new AuthModuleDigest ()
		};

		bool disposed;

		// Now all hail Java developers! Get this... HttpURLClient defaults to accepting AND
		// uncompressing the gzip content encoding UNLESS you set the Accept-Encoding header to ANY
		// value. So if we set it to 'gzip' below we WILL get gzipped stream but HttpURLClient will NOT
		// uncompress it any longer, doh. And they don't support 'deflate' so we need to handle it ourselves.
		bool decompress_here;

		URL java_url;
		URLConnection java_connection;

		/// <summary>
		/// <para>
		/// Gets or sets the pre authentication data for the request. This property must be set by the application
		/// before the request is made. Generally the value can be taken from <see cref="RequestedAuthentication"/>
		/// after the initial request, without any authentication data, receives the authorization request from the
		/// server. The application must then store credentials in instance of <see cref="AuthenticationData"/> and
		/// assign the instance to this propery before retrying the request.
		/// </para>
		/// <para>
		/// The property is never set by AndroidClientHandler.
		/// </para>
		/// </summary>
		/// <value>The pre authentication data.</value>
		public AuthenticationData PreAuthenticationData { get; set; }
		
		/// <summary>
		/// If the website requires authentication, this property will contain data about each scheme supported
		/// by the server after the response. Note that unauthorized request will return a valid response - you
		/// need to check the status code and and (re)configure AndroidClientHandler instance accordingly by providing
		/// both the credentials and the authentication scheme by setting the <see cref="PreAuthenticationData"/> 
		/// property. If AndroidClientHandler is not able to detect the kind of authentication scheme it will store an
		/// instance of <see cref="AuthenticationData"/> with its <see cref="AuthenticationData.Scheme"/> property
		/// set to <c>AuthenticationScheme.Unsupported</c> and the application will be responsible for providing an
		/// instance of <see cref="IAndroidAuthenticationModule"/> which handles this kind of authorization scheme
		/// (<see cref="AuthenticationData.AuthModule"/>
		/// </summary>
		public IList <AuthenticationData> RequestedAuthentication { get; private set; }

		/// <summary>
		/// Server authentication response indicates that the request to authorize comes from a proxy if this property is <c>true</c>.
		/// All the instances of <see cref="AuthenticationData"/> stored in the <see cref="RequestedAuthentication"/> property will
		/// have their <see cref="AuthenticationData.UseProxyAuthentication"/> preset to the same value as this property.
		/// </summary>
		public bool ProxyAuthenticationRequested { get; private set; }

		/// <summary>
		/// If <c>true</c> then the server requested authorization and the application must use information
		/// found in <see cref="RequestedAuthentication"/> to set the value of <see cref="PreAuthenticationData"/>
		/// </summary>
		public bool RequestNeedsAuthorization {
			get { return RequestedAuthentication?.Count > 0; }
		}

		/// <summary>
		/// <para>
		/// If the request is to the server protected with a self-signed (or otherwise untrusted) SSL certificate, the request will
		/// fail security chain verification unless the application provides either the CA certificate of the entity which issued the 
		/// server's certificate or, alternatively, provides the server public key. Whichever the case, the certificate(s) must be stored
		/// in this property in order for AndroidClientHandler to configure the request to accept the server certificate.</para>
		/// <para>AndroidClientHandler uses a custom <see cref="KeyStore"/> and <see cref="TrustManagerFactory"/> to configure the connection. 
		/// If, however, the application requires finer control over the SSL configuration (e.g. it implements its own TrustManager) then
		/// it should leave this property empty and instead derive a custom class from AndroidClientHandler and override, as needed, the 
		/// <see cref="ConfigureTrustManagerFactory"/>, <see cref="ConfigureKeyManagerFactory"/> and <see cref="ConfigureKeyStore"/> methods
		/// instead</para>
		/// </summary>
		/// <value>The trusted certs.</value>
		public IList <Certificate> TrustedCerts { get; set; }

		protected override void Dispose (bool disposing)
		{
			disposed  = true;
			if (java_connection != null) {
				java_connection.Dispose ();
				java_connection = null;
			}

			if (java_url != null) {
				java_url.Dispose ();
				java_url = null;
			}

			base.Dispose (disposing);
		}

		protected void AssertSelf ()
		{
			if (!disposed)
				return;
			throw new ObjectDisposedException (nameof (AndroidClientHandler));
		}

		/// <summary>
		/// Creates, configures and processes an asynchronous request to the indicated resource.
		/// </summary>
		/// <returns>Task in which the request is executed</returns>
		/// <param name="request">Request provided by <see cref="System.Net.Http.HttpClient"/></param>
		/// <param name="cancellationToken">Cancellation token.</param>
		protected override async Task <HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
		{
			AssertSelf ();
			if (request == null)
				throw new ArgumentNullException (nameof (request));
			
			if (!request.RequestUri.IsAbsoluteUri)
				throw new ArgumentException ("Must represent an absolute URI", "request");

			/*using (*/java_url = new URL (request.RequestUri.ToString ());/*) {*/
				/*using (*/java_connection = java_url.OpenConnection ();/*) {*/
					HttpURLConnection httpConnection = SetupRequestInternal (request, java_connection);
					return await ProcessRequest (request, httpConnection, cancellationToken);
				/*}
			}*/
		}
		
		async Task <HttpResponseMessage> ProcessRequest (HttpRequestMessage request, HttpURLConnection httpConnection, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested ();
			httpConnection.InstanceFollowRedirects = AllowAutoRedirect;
			RequestedAuthentication = null;
			ProxyAuthenticationRequested = false;
			
			return await Task<HttpResponseMessage>.Factory.StartNew (() => DoProcessRequest (request, httpConnection, cancellationToken), cancellationToken).ConfigureAwait (false);
		}
		
		HttpResponseMessage DoProcessRequest (HttpRequestMessage request, HttpURLConnection httpConnection, CancellationToken cancellationToken)
		{
			if (Logger.LogNet)
				Logger.Log (LogLevel.Info, LOG_APP, $"{this}.DoProcessRequest ()");
			httpConnection.RequestMethod = request.Method.ToString ();
			try {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"  connecting");
				httpConnection.Connect ();
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"  connected");
			} catch (Java.Net.ConnectException ex) {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Connection exception {ex}");
				// Wrap it nicely in a "standard" exception so that it's compatible with HttpClientHandler
				throw new WebException (ex.Message, ex, WebExceptionStatus.ConnectFailure, null);
			}

			var statusCode = (HttpStatusCode)httpConnection.ResponseCode;
			var connectionUri = new Uri (httpConnection.URL.ToString ());

			// If the request was redirected we need to put the new URL in the request
			request.RequestUri = connectionUri;
			var ret = new AndroidHttpResponseMessage {
				RequestMessage = request,
				ReasonPhrase = httpConnection.ResponseMessage,
				StatusCode = statusCode,
			};

			if (Logger.LogNet)
				Logger.Log (LogLevel.Info, LOG_APP, $"Status code: {statusCode}");
			if (statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.ProxyAuthenticationRequired) {
				// We don't resend the request since that would require new set of credentials if the
				// ones provided in Credentials are invalid (or null) and that, in turn, may require asking the
				// user which is not something that should be taken care of by us and in this
				// context. The application should be responsible for this.
				// HttpClientHandler throws an exception in this instance, but I think it's not a good
				// idea. We'll return the response message with all the information required by the
				// application to fill in the blanks and provide the requested credentials instead.
				//
				// We should return the body of the response too but, alas, the Java client will throw
				// a, wait for it, FileNotFound exception if we attempt to access the input stream. So
				// no body, just a dummy. Java FTW!
				ret.Content = new StringContent ("Unauthorized", Encoding.ASCII);
				CopyHeaders (httpConnection, ret);

				if (ret.Headers.WwwAuthenticate != null) {
					ProxyAuthenticationRequested = false;
					CollectAuthInfo (ret.Headers.WwwAuthenticate);
				} else if (ret.Headers.ProxyAuthenticate != null) {
					ProxyAuthenticationRequested = true;
					CollectAuthInfo (ret.Headers.ProxyAuthenticate);
				}

				ret.RequestedAuthentication = RequestedAuthentication;
				return ret;
			}

			if (!IsErrorStatusCode (statusCode)) {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Reading...");
				Stream inputStream = new BufferedStream (httpConnection.InputStream);
				if (decompress_here) {
					string[] encodings = httpConnection.ContentEncoding?.Split (',');
					if (encodings != null) {
						if (encodings.Contains (GZIP_ENCODING, StringComparer.OrdinalIgnoreCase))
							inputStream = new GZipStream (inputStream, CompressionMode.Decompress);
						else if (encodings.Contains (DEFLATE_ENCODING, StringComparer.OrdinalIgnoreCase))
							inputStream = new DeflateStream (inputStream, CompressionMode.Decompress);
					}
				}
				ret.Content = new StreamContent (inputStream);
			} else {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Status code is {statusCode}, returning empty content");
				// For 400 >= response code <= 599 the Java client throws the FileNotFound exeption when attempting to read from the connection
				// Client tests require we return no content here
				ret.Content = new StringContent (String.Empty, Encoding.ASCII);
			}
			CopyHeaders (httpConnection, ret);

			IEnumerable <string> cookieHeaderValue;
			if (!UseCookies || CookieContainer == null || !ret.Headers.TryGetValues ("Set-Cookie", out cookieHeaderValue) || cookieHeaderValue == null) {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"No cookies");
				return ret;
			}

			try {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Parsing cookies");
				CookieContainer.SetCookies (connectionUri, String.Join (",", cookieHeaderValue));
			} catch (Exception ex) {
				// We don't want to terminate the response because of a bad cookie, hence just reporting
				// the issue. We might consider adding a virtual method to let the user handle the
				// issue, but not sure if it's really needed. Set-Cookie header will be part of the
				// header collection so the user can always examine it if they spot an error.
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Failed to parse cookies in the server response. {ex.GetType ()}: {ex.Message}");
			}

			if (Logger.LogNet)
				Logger.Log (LogLevel.Info, LOG_APP, $"Returning");
			return ret;
		}

		bool IsErrorStatusCode (HttpStatusCode statusCode)
		{
			return (int)statusCode >= 400 && (int)statusCode <= 599;
		}

		void CollectAuthInfo (HttpHeaderValueCollection <AuthenticationHeaderValue> headers)
		{
			var authData = new List <AuthenticationData> (headers.Count);

			foreach (AuthenticationHeaderValue ahv in headers) {
				var data = new AuthenticationData {
					Scheme = GetAuthScheme (ahv.Scheme),
					Challenge = $"{ahv.Scheme} {ahv.Parameter}",
					UseProxyAuthentication = ProxyAuthenticationRequested
				};
				authData.Add (data);
			}

			RequestedAuthentication = authData.AsReadOnly ();
		}
		
		AuthenticationScheme GetAuthScheme (string scheme)
		{
			if (String.Compare ("basic", scheme, StringComparison.OrdinalIgnoreCase) == 0)
				return AuthenticationScheme.Basic;
			if (String.Compare ("digest", scheme, StringComparison.OrdinalIgnoreCase) == 0)
				return AuthenticationScheme.Digest;

			return AuthenticationScheme.Unsupported;
		}
		
		void CopyHeaders (HttpURLConnection httpConnection, HttpResponseMessage response)
		{
			IDictionary <string, IList <string>> headers = httpConnection.HeaderFields;
			foreach (string key in headers.Keys) {
				if (key == null) // First header entry has null key, it corresponds to the response message
					continue;

				HttpHeaders item_headers;
				string kind;
				if (known_content_headers.Contains (key)) {
					kind = "content";
					item_headers = response.Content.Headers;
				} else {
					kind = "response";
					item_headers = response.Headers;
				}
				item_headers.TryAddWithoutValidation (key, headers [key]);
			}
		}

		/// <summary>
		/// Configure the <see cref="HttpURLConnection"/> before the request is sent. This method is meant to be overriden
		/// by applications which need to perform some extra configuration steps on the connection. It is called with all
		/// the request headers set, pre-authentication performed (if applicable) but before the request body is set 
		/// (e.g. for POST requests). The default implementation in AndroidClientHandler does nothing.
		/// </summary>
		/// <param name="request">Request data</param>
		/// <param name="conn">Pre-configured connection instance</param>
		protected virtual void SetupRequest (HttpRequestMessage request, HttpURLConnection conn)
		{
			AssertSelf ();
		}

		/// <summary>
		/// Configures the key store. The <paramref name="keyStore"/> parameter is set to instance of <see cref="KeyStore"/>
		/// created using the <see cref="KeyStore.DefaultType"/> type and with populated with certificates provided in the <see cref="TrustedCerts"/>
		/// property. AndroidClientHandler implementation simply returns the instance passed in the <paramref name="keyStore"/> parameter
		/// </summary>
		/// <returns>The key store.</returns>
		/// <param name="keyStore">Key store to configure.</param>
		protected virtual KeyStore ConfigureKeyStore (KeyStore keyStore)
		{
			AssertSelf ();

			return keyStore;
		}

		/// <summary>
		/// Create and configure an instance of <see cref="KeyManagerFactory"/>. The <paramref name="keyStore"/> parameter is set to the
		/// return value of the <see cref="ConfigureKeyStore"/> method, so it might be null if the application overrode the method and provided
		/// no key store. It will not be <c>null</c> when the default implementation is used. The application can return <c>null</c> here since
		/// KeyManagerFactory is not required for the custom SSL configuration, but it might be used by the application to implement a more advanced
		/// mechanism of key management.
		/// </summary>
		/// <returns>The key manager factory or <c>null</c>.</returns>
		/// <param name="keyStore">Key store.</param>
		protected virtual KeyManagerFactory ConfigureKeyManagerFactory (KeyStore keyStore)
		{
			AssertSelf ();

			return null;
		}

		/// <summary>
		/// Create and configure an instance of <see cref="TrustManagerFactory"/>. The <paramref name="keyStore"/> parameter is set to the
		/// return value of the <see cref="ConfigureKeyStore"/> method, so it might be null if the application overrode the method and provided
		/// no key store. It will not be <c>null</c> when the default implementation is used. The application can return <c>null</c> from this 
		/// method in which case AndroidClientHandler will create its own instance of the trust manager factory provided that the <see cref="TrustCerts"/>
		/// list contains at least one valid certificate. If there are no valid certificates and this method returns <c>null</c>, no custom 
		/// trust manager will be created since that would make all the HTTPS requests fail.
		/// </summary>
		/// <returns>The trust manager factory.</returns>
		/// <param name="keyStore">Key store.</param>
		protected virtual TrustManagerFactory ConfigureTrustManagerFactory (KeyStore keyStore)
		{
			AssertSelf ();

			return null;
		}

		void AppendEncoding (string encoding, ref List <string> list)
		{
			if (list == null)
				list = new List <string> ();
			if (list.Contains (encoding))
				return;
			list.Add (encoding);
		}
		
		HttpURLConnection SetupRequestInternal (HttpRequestMessage request, URLConnection conn)
		{
			if (conn == null)
				throw new ArgumentNullException (nameof (conn));
			var httpConnection = conn.JavaCast <HttpURLConnection> ();
			if (httpConnection == null)
				throw new InvalidOperationException ($"Unsupported URL scheme {conn.URL.Protocol}");

			// SSL context must be set up as soon as possible, before adding any content or
			// headers. Otherwise Java won't use the socket factory
			SetupSSL (httpConnection as HttpsURLConnection);
			if (request.Content != null)
				AddHeaders (httpConnection, request.Content.Headers);
			AddHeaders (httpConnection, request.Headers);
			
			List <string> accept_encoding = null;

			decompress_here = false;
			if ((AutomaticDecompression & DecompressionMethods.GZip) != 0) {
				AppendEncoding (GZIP_ENCODING, ref accept_encoding);
				decompress_here = true;
			}
			
			if ((AutomaticDecompression & DecompressionMethods.Deflate) != 0) {
				AppendEncoding (DEFLATE_ENCODING, ref accept_encoding);
				decompress_here = true;
			}

			if (AutomaticDecompression == DecompressionMethods.None) {
				accept_encoding?.Clear ();
				AppendEncoding (IDENTITY_ENCODING, ref accept_encoding); // Turns off compression for the Java client
			}

			if (accept_encoding?.Count > 0)
				httpConnection.SetRequestProperty ("Accept-Encoding", String.Join (",", accept_encoding));

			if (UseCookies && CookieContainer != null) {
				string cookieHeaderValue = CookieContainer.GetCookieHeader (request.RequestUri);
				if (!String.IsNullOrEmpty (cookieHeaderValue))
					httpConnection.SetRequestProperty ("Cookie", cookieHeaderValue);
			}
			
			HandlePreAuthentication (httpConnection);
			SetupRequest (request, httpConnection);
			SetupRequestBody (httpConnection, request);
			
			return httpConnection;
		}

		void SetupSSL (HttpsURLConnection httpsConnection)
		{
			if (httpsConnection == null)
				return;

			KeyStore keyStore = KeyStore.GetInstance (KeyStore.DefaultType);
			keyStore.Load (null, null);
			bool gotCerts = TrustedCerts?.Count > 0;
			if (gotCerts) {
				for (int i = 0; i < TrustedCerts.Count; i++) {
					Certificate cert = TrustedCerts [i];
					if (cert == null)
						continue;
					keyStore.SetCertificateEntry ($"ca{i}", cert);
				}
			}
			keyStore = ConfigureKeyStore (keyStore);
			KeyManagerFactory kmf = ConfigureKeyManagerFactory (keyStore);
			TrustManagerFactory tmf = ConfigureTrustManagerFactory (keyStore);

			if (tmf == null) {
				// If there are no certs and no trust manager factory, we can't use a custom manager
				// because it will cause all the HTTPS requests to fail because of unverified trust
				// chain
				if (!gotCerts)
					return;
				
				tmf = TrustManagerFactory.GetInstance (TrustManagerFactory.DefaultAlgorithm);
				tmf.Init (keyStore);
			}

			SSLContext context = SSLContext.GetInstance ("TLS");
			context.Init (kmf?.GetKeyManagers (), tmf.GetTrustManagers (), null);
			httpsConnection.SSLSocketFactory = context.SocketFactory;
		}
		
		void HandlePreAuthentication (HttpURLConnection httpConnection)
		{
			AuthenticationData data = PreAuthenticationData;
			if (!PreAuthenticate || data == null)
				return;

			ICredentials creds = data.UseProxyAuthentication ? Proxy?.Credentials : Credentials;
			if (creds == null) {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Authentication using scheme {data.Scheme} requested but no credentials found. No authentication will be performed");
				return;
			}

			IAndroidAuthenticationModule auth = data.Scheme == AuthenticationScheme.Unsupported ? data.AuthModule : authModules.Find (m => m?.Scheme == data.Scheme);
			if (auth == null) {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Authentication module for scheme '{data.Scheme}' not found. No authentication will be performed");
				return;
			}

			Authorization authorization = auth.Authenticate (data.Challenge, httpConnection, creds);
			if (authorization == null) {
				if (Logger.LogNet)
					Logger.Log (LogLevel.Info, LOG_APP, $"Authorization module {auth.GetType ()} for scheme {data.Scheme} returned no authorization");
				return;
			}

			if (Logger.LogNet)
				Logger.Log (LogLevel.Info, LOG_APP, $"Authentication header '{data.UseProxyAuthentication ? "Proxy-Authorization" : "Authorization"}' will be set to '{authorization.Message}'");
			httpConnection.SetRequestProperty (data.UseProxyAuthentication ? "Proxy-Authorization" : "Authorization", authorization.Message);
		}

		void AddHeaders (HttpURLConnection conn, HttpHeaders headers)
		{
			if (headers == null)
				return;

			foreach (KeyValuePair<string, IEnumerable<string>> header in headers) {
				conn.SetRequestProperty (header.Key, header.Value != null ? String.Join (",", header.Value) : String.Empty);
			}
		}
		
		void SetupRequestBody (HttpURLConnection conn, HttpRequestMessage request)
		{
			if (request.Content == null) {
				// Pilfered from System.Net.Http.HttpClientHandler:SendAync
				if (HttpMethod.Post.Equals (request.Method) || HttpMethod.Put.Equals (request.Method) || HttpMethod.Delete.Equals (request.Method)) {
					// Explicitly set this to make sure we're sending a "Content-Length: 0" header.
					// This fixes the issue that's been reported on the forums:
					// http://forums.xamarin.com/discussion/17770/length-required-error-in-http-post-since-latest-release
					conn.SetRequestProperty ("Content-Length", "0");
				}
				return;
			}
			
			conn.DoOutput = true;
			long? contentLength = request.Content.Headers.ContentLength;
			if (contentLength != null)
				conn.SetFixedLengthStreamingMode ((int)contentLength);
			else
				conn.SetChunkedStreamingMode (0);
		}
	}
}
