package org.jellyfin.androidtv.pickyfin

import android.content.Context
import androidx.core.content.edit
import kotlinx.coroutines.Dispatchers
import okhttp3.OkHttpClient
import okhttp3.Request
import org.jellyfin.sdk.api.client.ApiClient
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import timber.log.Timber
import java.net.URI
import java.util.UUID
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
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
 * Every film's scores from Pickyfin Server: the Letterboxd rating (from the
 * letterboxd-collector service the server reads) and Rotten Tomatoes from OMDb.
 *
 * The whole list is downloaded at start and again after the interval the server
 * sends with it (its Letterboxd setting, 6 hours by default), and kept on disk,
 * so ratings show at once and survive a restart or a server that's briefly down.
 * Asked with the viewer's own Jellyfin token, so nothing else needs to be set up.
 */
class PickyfinScores(
	context: Context,
	private val store: PickyfinServerStore,
	accessHeaders: AccessHeaderStore,
	private val api: ApiClient,
) {
	data class Scores(
		val letterboxd: Double?,
		val rottenTomatoes: Int?,
	)

	private val client = OkHttpClient.Builder()
		.addInterceptor(AccessHeaderInterceptor(accessHeaders))
		.connectTimeout(5, TimeUnit.SECONDS)
		.readTimeout(30, TimeUnit.SECONDS)
		.build()

	private val file = File(context.filesDir, "pickyfin_ratings.json")
	private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
	private val loading = AtomicBoolean(false)

	private val _ratings = MutableStateFlow<Map<UUID, Scores>>(emptyMap())
	val ratings: StateFlow<Map<UUID, Scores>> = _ratings

	/** The server the list on disk came from, when it was downloaded, and the interval to the next. */
	@Volatile private var source: String? = null
	@Volatile private var fetchedAt = 0L
	@Volatile private var hours = DEFAULT_HOURS
	@Volatile private var failedAt = 0L

	init {
		scope.launch { readFile() }
	}

	fun address(): String? = store.address.ifBlank { null } ?: PickyfinServerStore.automatic(api.baseUrl)

	/** Downloads the list when it's older than the server's interval (or from another server). */
	fun refreshIfDue() {
		val now = System.currentTimeMillis()
		val base = address() ?: return
		val due = source != base || now - fetchedAt > hours * HOUR_MS
		if (!due || now - failedAt < RETRY_MS) return
		if (!loading.compareAndSet(false, true)) return
		scope.launch {
			try {
				download(base)
			} finally {
				loading.set(false)
			}
		}
	}

	/** Forget the list, e.g. after the address changed. */
	fun reset() {
		_ratings.value = emptyMap()
		source = null
		fetchedAt = 0
		failedAt = 0
		scope.launch { file.delete() }
	}

	private fun download(base: String) {
		val token = api.accessToken ?: return
		try {
			val request = Request.Builder()
				.url("$base/api/tv/ratings")
				.header("X-Emby-Token", token)
				.build()
			client.newCall(request).execute().use { response ->
				if (!response.isSuccessful) {
					failedAt = System.currentTimeMillis()
					return
				}
				val list = json.decodeFromString<RatingsList>(response.body?.string().orEmpty())
				apply(list, base, System.currentTimeMillis())
				file.writeText(json.encodeToString(Saved(base, fetchedAt, list)))
			}
		} catch (error: Exception) {
			Timber.d(error, "Pickyfin Server not reached at %s", base)
			failedAt = System.currentTimeMillis()
		}
	}

	private fun readFile() {
		try {
			if (!file.exists()) return
			val saved = json.decodeFromString<Saved>(file.readText())
			apply(saved.list, saved.source, saved.at)
		} catch (error: Exception) {
			Timber.d(error, "Pickyfin: saved ratings unreadable")
		}
	}

	private fun apply(list: RatingsList, from: String, at: Long) {
		_ratings.value = parse(list)
		hours = list.hours.coerceIn(1, 168)
		source = from
		fetchedAt = at
		failedAt = 0
	}

	companion object {
		private const val DEFAULT_HOURS = 6
		private const val HOUR_MS = 60 * 60 * 1000L
		/** After the server didn't answer, try again after this long. */
		private const val RETRY_MS = 10 * 60 * 1000L

		private val json = Json { ignoreUnknownKeys = true }

		fun parse(body: String): Map<UUID, Scores> = parse(json.decodeFromString<RatingsList>(body))

		fun parse(list: RatingsList): Map<UUID, Scores> = list.films.mapNotNull { film ->
			val id = runCatching { UUID.fromString(film.id) }.getOrNull() ?: return@mapNotNull null
			val letterboxd = film.letterboxd?.takeIf { it > 0 && it <= 5 }
			val rottenTomatoes = film.rottenTomatoes?.takeIf { it in 0..100 }
			if (letterboxd == null && rottenTomatoes == null) null else id to Scores(letterboxd, rottenTomatoes)
		}.toMap()
	}
}

/** What /api/tv/ratings sends: the interval, and per film I = item id, L = Letterboxd, R = Rotten Tomatoes. */
@Serializable
data class RatingsList(
	@SerialName("Hours") val hours: Int = 6,
	@SerialName("Films") val films: List<Film> = emptyList(),
) {
	@Serializable
	data class Film(
		@SerialName("I") val id: String,
		@SerialName("L") val letterboxd: Double? = null,
		@SerialName("R") val rottenTomatoes: Int? = null,
	)
}

/** The list as kept on disk, with where and when it came from. */
@Serializable
private data class Saved(val source: String, val at: Long, val list: RatingsList)
