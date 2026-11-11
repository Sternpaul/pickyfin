package org.jellyfin.androidtv.pickyfin

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.widget.Toast
import androidx.core.content.FileProvider
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleCoroutineScope
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import org.jellyfin.androidtv.BuildConfig
import org.jellyfin.androidtv.R
import timber.log.Timber
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * Checks GitHub Releases for a newer build of this app and offers to install it.
 *
 * A sideloaded app can't update itself silently on Fire TV or Android TV: the
 * system installer always asks the user to confirm. So this asks once, downloads
 * the APK and hands it to the installer, which shows its own one-click prompt.
 *
 * The repository may hold releases of several Pickyfin apps, so only releases
 * tagged tv-v<version> count.
 *
 * It checks every time the app is opened, including when it comes back from the
 * background (Fire TV keeps apps in memory, so that's the usual way it opens), but
 * only once per opening: coming back from the player doesn't ask again.
 *
 * "Remind me later" waits 7 days before asking about any update again;
 * "Skip this version" never asks about that release again.
 *
 * Builds without a configured repository (local builds) never check.
 */
object Updater {
	private const val PREFERENCES = "pickyfin_updater"
	private const val KEY_SKIPPED_TAG = "skipped_tag"
	private const val KEY_REMIND_AFTER = "remind_after"
	private const val REMIND_LATER_MS = 7L * 24 * 60 * 60 * 1000
	private const val TAG_PREFIX = "tv-v"

	private val json = Json { ignoreUnknownKeys = true }

	// A client of its own: update checks go to GitHub and must never carry the
	// server's access header.
	private val http by lazy {
		OkHttpClient.Builder()
			.connectTimeout(15, TimeUnit.SECONDS)
			.readTimeout(60, TimeUnit.SECONDS)
			.build()
	}

	@Serializable
	private data class Release(
		@SerialName("tag_name") val tagName: String,
		val draft: Boolean = false,
		val prerelease: Boolean = false,
		val assets: List<Asset> = emptyList(),
	)

	@Serializable
	private data class Asset(
		val name: String,
		@SerialName("browser_download_url") val downloadUrl: String,
	)

	// Whether this opening of the app was already checked; reset when the whole app
	// goes to the background
	private var checkedThisOpening = false
	private var watchingAppLifecycle = false

	/** Call when the main screen starts; checks once per opening of the app. */
	fun checkOnOpen(activity: Activity, scope: LifecycleCoroutineScope) {
		val repository = BuildConfig.PICKYFIN_UPDATE_REPOSITORY
		if (repository.isBlank()) return

		if (!watchingAppLifecycle) {
			watchingAppLifecycle = true
			ProcessLifecycleOwner.get().lifecycle.addObserver(object : DefaultLifecycleObserver {
				override fun onStop(owner: LifecycleOwner) {
					checkedThisOpening = false
				}
			})
		}
		if (checkedThisOpening) return
		checkedThisOpening = true

		val preferences = activity.getSharedPreferences(PREFERENCES, Activity.MODE_PRIVATE)
		if (System.currentTimeMillis() < preferences.getLong(KEY_REMIND_AFTER, 0)) return

		scope.launch {
			val releases = withContext(Dispatchers.IO) { tvReleases(repository) }
			if (releases == null) {
				// Not reachable (e.g. network not up yet after waking): try again next time
				checkedThisOpening = false
				return@launch
			}

			val release = newest(releases) ?: return@launch
			val apk = release.assets.firstOrNull { it.name.endsWith(".apk") } ?: return@launch
			val version = release.tagName.removePrefix(TAG_PREFIX)
			if (versionCodeOf(version) <= BuildConfig.VERSION_CODE) return@launch
			if (preferences.getString(KEY_SKIPPED_TAG, null) == release.tagName) return@launch
			if (activity.isFinishing || activity.isDestroyed) return@launch

			AlertDialog.Builder(activity)
				.setTitle(R.string.pickyfin_update_title)
				.setMessage(activity.getString(R.string.pickyfin_update_message, version, BuildConfig.VERSION_NAME))
				.setPositiveButton(R.string.pickyfin_update_install) { _, _ -> download(activity, scope, apk) }
				.setNegativeButton(R.string.pickyfin_update_later) { _, _ ->
					preferences.edit().putLong(KEY_REMIND_AFTER, System.currentTimeMillis() + REMIND_LATER_MS).apply()
				}
				.setNeutralButton(R.string.pickyfin_update_skip) { _, _ ->
					preferences.edit().putString(KEY_SKIPPED_TAG, release.tagName).apply()
				}
				.show()
		}
	}

	private fun tvReleases(repository: String): List<Release>? = try {
		val request = Request.Builder()
			.url("https://api.github.com/repos/$repository/releases?per_page=50")
			.header("Accept", "application/vnd.github+json")
			.build()
		http.newCall(request).execute().use { response ->
			if (!response.isSuccessful) null
			else json.decodeFromString<List<Release>>(response.body?.string() ?: return@use null)
		}
	} catch (error: Exception) {
		Timber.w(error, "Pickyfin: update check failed")
		null
	}

	private fun download(activity: Activity, scope: LifecycleCoroutineScope, apk: Asset) {
		Toast.makeText(activity, R.string.pickyfin_update_downloading, Toast.LENGTH_LONG).show()

		scope.launch {
			val file = withContext(Dispatchers.IO) {
				try {
					val directory = File(activity.cacheDir, "updates").apply {
						deleteRecursively()
						mkdirs()
					}
					val target = File(directory, "update.apk")
					http.newCall(Request.Builder().url(apk.downloadUrl).build()).execute().use { response ->
						if (!response.isSuccessful) return@withContext null
						val body = response.body ?: return@withContext null
						target.outputStream().use { out -> body.byteStream().copyTo(out) }
					}
					target
				} catch (error: Exception) {
					Timber.w(error, "Pickyfin: update download failed")
					null
				}
			}

			if (file == null) {
				Toast.makeText(activity, R.string.pickyfin_update_failed, Toast.LENGTH_LONG).show()
				return@launch
			}
			install(activity, file)
		}
	}

	private fun install(activity: Activity, file: File) {
		// Android 8+ asks once per app whether it may install updates
		if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !activity.packageManager.canRequestPackageInstalls()) {
			Toast.makeText(activity, R.string.pickyfin_update_allow, Toast.LENGTH_LONG).show()
			try {
				activity.startActivity(
					Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${activity.packageName}"))
				)
			} catch (error: Exception) {
				Timber.w(error, "Pickyfin: could not open the install permission screen")
			}
			// Coming back from the settings screen opens the app again, which checks
			// again and offers the update once permission is given
			return
		}

		val uri = FileProvider.getUriForFile(activity, "${activity.packageName}.pickyfin.updates", file)
		val intent = Intent(Intent.ACTION_VIEW)
			.setDataAndType(uri, "application/vnd.android.package-archive")
			.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
		try {
			activity.startActivity(intent)
		} catch (error: Exception) {
			Timber.w(error, "Pickyfin: no installer available")
			Toast.makeText(activity, R.string.pickyfin_update_failed, Toast.LENGTH_LONG).show()
		}
	}

	/** The newest published TV release, or null if there is none. */
	private fun newest(releases: List<Release>): Release? = releases
		.filter { !it.draft && !it.prerelease && it.tagName.startsWith(TAG_PREFIX) }
		.maxByOrNull { versionCodeOf(it.tagName.removePrefix(TAG_PREFIX)) }
		?.takeIf { versionCodeOf(it.tagName.removePrefix(TAG_PREFIX)) > 0 }

	/** Same scheme as the build's version code: 0.19.10-pickyfin.2 -> 191002. */
	internal fun versionCodeOf(version: String): Int = try {
		getVersionCode(version)
	} catch (error: Exception) {
		0
	}

	private fun getVersionCode(versionName: String): Int {
		val core = versionName.substringBefore('-')
		val preRelease = versionName.substringAfter('-', "").ifBlank { null }
		val (major, minor, patch) = core.split('.').map { it.toInt() }.let { Triple(it[0], it[1], it[2]) }
		val build = preRelease?.substringAfter('.')?.toIntOrNull() ?: 99
		return major * 1000000 + minor * 10000 + patch * 100 + build
	}
}
