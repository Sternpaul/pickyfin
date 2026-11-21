package org.jellyfin.androidtv.pickyfin

import android.content.Context
import androidx.core.content.edit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.jellyfin.sdk.api.client.ApiClient
import org.json.JSONObject
import timber.log.Timber
import java.net.URI
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/**
 * Where Pickyfin Server runs. Empty means automatic: the Jellyfin server's host on
 * port 8097, which is where it runs when it sits next to Jellyfin.
 */
class PickyfinServerStore(context: Context) {
	private val preferences = context.getSharedPreferences("pickyfin_server", Context.MODE_PRIVATE)

	var address: String
		get() = preferences.getString(KEY_ADDRESS, null).orEmpty()
		set(value) = preferences.edit { putString(KEY_ADDRESS, value.trim().trimEnd('/')) }

	companion object {
		private const val KEY_ADDRESS = "address"
		const val DEFAULT_PORT = 8097

		/** "http://192.168.1.5:8096" → "http://192.168.1.5:8097". */
		fun automatic(jellyfinAddress: String?): String? = try {
			val uri = URI(jellyfinAddress ?: return null)
			if (uri.host.isNullOrBlank()) null else "${uri.scheme ?: "http"}://${uri.host}:$DEFAULT_PORT"
		} catch (_: Exception) {
			null
		}

		/** Accepts "192.168.1.5:8097" as well as a full address. */
		fun normalize(typed: String): String {
			val trimmed = typed.trim().trimEnd('/')
			return if (trimmed.isEmpty() || trimmed.contains("://")) trimmed else "http://$trimmed"
		}
	}
}

/**
 * A film's scores from Pickyfin Server: the Letterboxd rating (from the
 * letterboxd-collector service the server reads) and Rotten Tomatoes from OMDb.
 * Asked with the viewer's own Jellyfin token, so nothing else needs to be set up.
 */
class PickyfinScores(
	private val store: PickyfinServerStore,
	accessHeaders: AccessHeaderStore,
	private val api: ApiClient,
) {
	data class Scores(
		val letterboxd: Double?,
		val letterboxdCount: Int?,
		val rottenTomatoes: Int?,
	)

	private val client = OkHttpClient.Builder()
		.addInterceptor(AccessHeaderInterceptor(accessHeaders))
		.connectTimeout(3, TimeUnit.SECONDS)
		.readTimeout(5, TimeUnit.SECONDS)
		.build()

	private val cache = ConcurrentHashMap<UUID, Scores>()
	private val none = Scores(null, null, null)

	/** After the server didn't answer, wait a bit before trying again, so details pages don't hang on it. */
	@Volatile
	private var quietUntil = 0L

	fun address(): String? = store.address.ifBlank { null } ?: PickyfinServerStore.automatic(api.baseUrl)

	fun cached(id: UUID): Scores? = cache[id]?.takeIf { it !== none }

	suspend fun get(id: UUID): Scores? {
		cache[id]?.let { return it.takeIf { s -> s !== none } }
		if (System.currentTimeMillis() < quietUntil) return null
		val base = address() ?: return null
		val token = api.accessToken ?: return null
		return withContext(Dispatchers.IO) {
			try {
				val request = Request.Builder()
					.url("$base/api/tv/films/$id")
					.header("X-Emby-Token", token)
					.build()
				client.newCall(request).execute().use { response ->
					when {
						response.isSuccessful -> parse(response.body?.string().orEmpty()).also { cache[id] = it }
						// Not a film Pickyfin knows
						response.code == 404 -> { cache[id] = none; null }
						else -> { quietUntil = System.currentTimeMillis() + QUIET_MS; null }
					}
				}
			} catch (error: Exception) {
				Timber.d(error, "Pickyfin Server not reached at %s", base)
				quietUntil = System.currentTimeMillis() + QUIET_MS
				null
			}
		}
	}

	/** Forget what was fetched, e.g. after the address changed. */
	fun reset() {
		cache.clear()
		quietUntil = 0
	}

	private fun parse(body: String): Scores {
		val json = JSONObject(body)
		val letterboxd = json.optJSONObject("Letterboxd")
		return Scores(
			letterboxd = letterboxd?.optDouble("Rating")?.takeIf { !it.isNaN() && it > 0 },
			letterboxdCount = letterboxd?.optInt("Count", -1)?.takeIf { it >= 0 },
			rottenTomatoes = json.optInt("RottenTomatoes", -1).takeIf { it in 0..100 },
		)
	}

	private companion object {
		const val QUIET_MS = 5 * 60 * 1000L
	}
}
