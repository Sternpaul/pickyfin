package org.jellyfin.androidtv.ui.settings.screen

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.stringResource
import org.jellyfin.androidtv.R
import org.jellyfin.androidtv.ui.base.Icon
import org.jellyfin.androidtv.ui.base.Text
import org.jellyfin.androidtv.ui.base.list.ListButton
import org.jellyfin.androidtv.ui.base.list.ListSection
import org.jellyfin.androidtv.ui.navigation.LocalRouter
import org.jellyfin.androidtv.ui.navigation.focus.focusKey
import org.jellyfin.androidtv.ui.settings.Routes
import org.jellyfin.androidtv.ui.settings.composable.SettingsColumn
import android.app.AlertDialog
import android.text.InputType
import android.widget.EditText
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import org.jellyfin.androidtv.pickyfin.PickyfinScores
import org.jellyfin.androidtv.pickyfin.PickyfinServerStore
import org.koin.compose.koinInject

@Composable
fun SettingsMainScreen() {
	val router = LocalRouter.current

	SettingsColumn {
		item {
			ListSection(
				overlineContent = { Text(stringResource(R.string.app_name).uppercase()) },
				headingContent = { Text(stringResource(R.string.settings)) },
				captionContent = { Text(stringResource(R.string.settings_description)) },
			)
		}

		item {
			ListButton(
				leadingContent = { Icon(painterResource(R.drawable.ic_users), contentDescription = null) },
				headingContent = { Text(stringResource(R.string.pref_login)) },
				onClick = { router.push(Routes.AUTHENTICATION) },
				modifier = Modifier.focusKey(Routes.AUTHENTICATION),
			)
		}

		item {
			ListButton(
				leadingContent = { Icon(painterResource(R.drawable.ic_adjust), contentDescription = null) },
				headingContent = { Text(stringResource(R.string.pref_customization)) },
				onClick = { router.push(Routes.CUSTOMIZATION) },
				modifier = Modifier.focusKey(Routes.CUSTOMIZATION),
			)
		}

		// TODO: Temporarily added to root - should be accessed via customization screen instead
		item {
			ListButton(
				leadingContent = { Icon(painterResource(R.drawable.ic_photos), contentDescription = null) },
				headingContent = { Text(stringResource(R.string.pref_screensaver)) },
				onClick = { router.push(Routes.CUSTOMIZATION_SCREENSAVER) },
				modifier = Modifier.focusKey(Routes.CUSTOMIZATION_SCREENSAVER),
			)
		}

		item {
			ListButton(
				leadingContent = { Icon(painterResource(R.drawable.ic_next), contentDescription = null) },
				headingContent = { Text(stringResource(R.string.pref_playback)) },
				onClick = { router.push(Routes.PLAYBACK) },
				modifier = Modifier.focusKey(Routes.PLAYBACK),
			)
		}

		item {
			ListButton(
				leadingContent = { Icon(painterResource(R.drawable.ic_error), contentDescription = null) },
				headingContent = { Text(stringResource(R.string.pref_telemetry_category)) },
				onClick = { router.push(Routes.TELEMETRY) },
				modifier = Modifier.focusKey(Routes.TELEMETRY),
			)

		}

		item { PickyfinServerButton() }

		item {
			ListButton(
				leadingContent = { Icon(painterResource(R.drawable.ic_jellyfin), contentDescription = null) },
				headingContent = { Text(stringResource(R.string.pref_about_title)) },
				onClick = { router.push(Routes.ABOUT) },
				modifier = Modifier.focusKey(Routes.ABOUT),
			)
		}
	}
}

/**
 * Where Pickyfin Server runs, for the Letterboxd rating on the details screen.
 * Automatic (the Jellyfin server's host, port 8097) fits most setups.
 */
@Composable
private fun PickyfinServerButton() {
	val context = LocalContext.current
	val store = koinInject<PickyfinServerStore>()
	val scores = koinInject<PickyfinScores>()
	var address by remember { mutableStateOf(store.address) }
	val automatic = stringResource(R.string.pickyfin_server_automatic, scores.address().orEmpty())

	ListButton(
		leadingContent = { Icon(painterResource(R.drawable.ic_star), contentDescription = null) },
		headingContent = { Text(stringResource(R.string.pickyfin_server_title)) },
		captionContent = { Text(address.ifBlank { automatic }) },
		onClick = {
			val input = EditText(context).apply {
				setText(address)
				hint = scores.address() ?: "http://192.168.1.20:8097"
				inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
				setSingleLine()
			}
			AlertDialog.Builder(context)
				.setTitle(R.string.pickyfin_server_title)
				.setMessage(R.string.pickyfin_server_message)
				.setView(input)
				.setPositiveButton(R.string.pickyfin_server_save) { _, _ ->
					store.address = PickyfinServerStore.normalize(input.text.toString())
					address = store.address
					scores.reset()
				}
				.setNeutralButton(R.string.pickyfin_server_use_automatic) { _, _ ->
					store.address = ""
					address = ""
					scores.reset()
				}
				.setNegativeButton(android.R.string.cancel, null)
				.show()
		},
	)
}
