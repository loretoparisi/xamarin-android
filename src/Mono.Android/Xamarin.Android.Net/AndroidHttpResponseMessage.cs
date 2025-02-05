using System.Collections.Generic;
using System.Net.Http;

namespace Xamarin.Android.Net
{
	/// <summary>
	/// A convenience wrapper around <see cref="System.Net.Http.HttpResponseMessage"/> returned by <see cref="AndroidClientHandler.SendAsync"/>
	/// that allows easy access to authentication data as returned by the server, if any.
	/// </summary>
	public class AndroidHttpResponseMessage : HttpResponseMessage
	{
		/// <summary>
		/// Set to the same value as <see cref="AndroidClientHandler.RequestedAuthentication"/>.
		/// </summary>
		/// <value>The requested authentication.</value>
		public IList <AuthenticationData> RequestedAuthentication { get; internal set; }

		/// <summary>
		/// Set to the same value as <see cref="AndroidClientHandler.RequestNeedsAuthorization"/>
		/// </summary>
		/// <value>The request needs authorization.</value>
		public bool RequestNeedsAuthorization {
			get { return RequestedAuthentication?.Count > 0; }
		}
	}
}
