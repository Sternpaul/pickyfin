package org.jellyfin.androidtv.pickyfin

import android.content.Context
import org.jellyfin.androidtv.R
import org.jellyfin.androidtv.constant.ChangeTriggerType
import org.jellyfin.androidtv.data.repository.ItemRepository
import org.jellyfin.androidtv.ui.browsing.BrowseRowDef
import org.jellyfin.androidtv.ui.home.HomeFragmentBrowseRowDefRow
import org.jellyfin.androidtv.ui.home.HomeFragmentRow
import org.jellyfin.sdk.api.client.ApiClient
import org.jellyfin.sdk.api.client.extensions.itemsApi
import org.jellyfin.sdk.model.api.BaseItemDto
import org.jellyfin.sdk.model.api.BaseItemKind
import org.jellyfin.sdk.model.api.request.GetItemsRequest
import timber.log.Timber

/**
 * The personal recommendation rows on the home screen.
 *
 * The AI Recommender server plugin writes each user's picks into private
 * playlists. This turns the ones meant for the home screen into rows, so the
 * user sees them without opening a playlist. A user without such playlists
 * (or a server without the plugin) simply gets no rows.
 */
object RecommendationRows {
	/** Playlist names that become rows, in the order they appear on screen. */
	private val rowOrder = listOf<(String) -> Boolean>(
		{ it.equals("Recommended for you", ignoreCase = true) },
		{ it.equals("For You", ignoreCase = true) },
		{ it.startsWith("Because You Watched", ignoreCase = true) },
	)

	private const val MAX_ROWS = 3
	private const val ITEMS_PER_ROW = 30

	suspend fun load(context: Context, api: ApiClient): List<HomeFragmentRow> = try {
		val playlists = api.itemsApi.getItems(
			GetItemsRequest(
				includeItemTypes = setOf(BaseItemKind.PLAYLIST),
				recursive = true,
				enableTotalRecordCount = false,
				enableImages = false,
			)
		).content.items

		pick(playlists).map { playlist -> row(context, playlist) }
	} catch (error: Exception) {
		Timber.w(error, "Pickyfin: could not load recommendation playlists")
		emptyList()
	}

	private fun pick(playlists: List<BaseItemDto>): List<BaseItemDto> = rowOrder
		.flatMap { matches -> playlists.filter { playlist -> playlist.name?.let(matches) == true } }
		.distinctBy { it.id }
		.take(MAX_ROWS)

	private fun row(context: Context, playlist: BaseItemDto): HomeFragmentRow {
		val title = when {
			playlist.name.equals("For You", ignoreCase = true) -> context.getString(R.string.pickyfin_row_recommended)
			else -> playlist.name.orEmpty()
		}

		val query = GetItemsRequest(
			parentId = playlist.id,
			fields = ItemRepository.browseFields,
			imageTypeLimit = 1,
			// Something the user has finished drops out at once, without waiting
			// for the plugin's next refresh
			isPlayed = false,
			limit = ITEMS_PER_ROW,
			enableTotalRecordCount = false,
		)

		return HomeFragmentBrowseRowDefRow(
			BrowseRowDef(
				title,
				query,
				0,
				false,
				true,
				arrayOf(ChangeTriggerType.MoviePlayback, ChangeTriggerType.TvPlayback),
			)
		)
	}
}
