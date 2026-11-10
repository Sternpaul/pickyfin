package org.jellyfin.androidtv.pickyfin

import android.content.Context
import androidx.core.content.edit
import okhttp3.Interceptor
import okhttp3.Response

/**
 * The access header a reverse proxy in front of the server expects, entered once
 * on the "add server" screen.
 *
 * It is stored together with the host it was entered for, and only ever sent to
 * that host: requests to anything else (update checks on GitHub, for example)
 * never carry it.
 */
data class AccessHeader(
	val name: String,
	val value: String,
	val host: String,
)

class AccessHeaderStore(context: Context) {
	private val preferences = context.getSharedPreferences("pickyfin_access_header", Context.MODE_PRIVATE)

	@Volatile
	private var cached: AccessHeader? = load()

	val current: AccessHeader? get() = cached

	fun save(name: String, value: String, serverAddress: String) {
		val host = hostOf(serverAddress)
		val header = if (name.isBlank() || value.isBlank() || host == null) null
		else AccessHeader(name.trim(), value.trim(), host)

		preferences.edit {
			if (header == null) clear()
			else {
				putString(KEY_NAME, header.name)
				putString(KEY_VALUE, header.value)
				putString(KEY_HOST, header.host)
			}
		}
		cached = header
	}

	private fun load(): AccessHeader? {
		val name = preferences.getString(KEY_NAME, null) ?: return null
		val value = preferences.getString(KEY_VALUE, null) ?: return null
		val host = preferences.getString(KEY_HOST, null) ?: return null
		return AccessHeader(name, value, host)
	}

	companion object {
		private const val KEY_NAME = "name"
		private const val KEY_VALUE = "value"
		private const val KEY_HOST = "host"

		/**
		 * The host part of what was typed into the address field, which may or may
		 * not carry a scheme, port or path ("media.example.com", "https://x.org:8920/jf").
		 */
		fun hostOf(address: String): String? = address.trim()
			.substringAfter("://")
			.substringBefore('/')
			.substringBefore('?')
			.let { authority ->
				// Bracketed IPv6 literal keeps its colons.
				if (authority.startsWith("[")) authority.substringBefore(']') + "]"
				else authority.substringBefore(':')
			}
			.lowercase()
			.ifBlank { null }
	}
}

class AccessHeaderInterceptor(private val store: AccessHeaderStore) : Interceptor {
	override fun intercept(chain: Interceptor.Chain): Response {
		val request = chain.request()
		val header = store.current
		if (header == null || !request.url.host.equals(header.host.trim('[', ']'), ignoreCase = true)) {
			return chain.proceed(request)
		}
		return chain.proceed(request.newBuilder().header(header.name, header.value).build())
	}
}
