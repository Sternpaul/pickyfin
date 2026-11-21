package org.jellyfin.androidtv.pickyfin

import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe

class PickyfinTests : FunSpec({
	test("AccessHeaderStore.hostOf() finds the host in whatever was typed") {
		AccessHeaderStore.hostOf("media.example.com") shouldBe "media.example.com"
		AccessHeaderStore.hostOf("https://Media.Example.com") shouldBe "media.example.com"
		AccessHeaderStore.hostOf("https://media.example.com:8920/jellyfin") shouldBe "media.example.com"
		AccessHeaderStore.hostOf("http://192.168.1.5:8096") shouldBe "192.168.1.5"
		AccessHeaderStore.hostOf("[fd00::1]:8096") shouldBe "[fd00::1]"
		AccessHeaderStore.hostOf("  ") shouldBe null
	}

	test("PickyfinServerStore finds Pickyfin Server next to Jellyfin, and reads typed addresses") {
		PickyfinServerStore.automatic("http://192.168.1.5:8096") shouldBe "http://192.168.1.5:8097"
		PickyfinServerStore.automatic("https://media.example.com/jellyfin") shouldBe "https://media.example.com:8097"
		PickyfinServerStore.automatic("http://[fd00::1]:8096") shouldBe "http://[fd00::1]:8097"
		PickyfinServerStore.automatic(null) shouldBe null
		PickyfinServerStore.normalize("192.168.1.20:8097/") shouldBe "http://192.168.1.20:8097"
		PickyfinServerStore.normalize("https://picky.example.com") shouldBe "https://picky.example.com"
		PickyfinServerStore.normalize("  ") shouldBe ""
	}

	test("PickyfinScores.parse() reads the server's ratings list") {
		val films = PickyfinScores.parse(
			"""{"Hours":6,"Films":[{"I":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","L":4.4,"R":91},{"I":"11111111-2222-3333-4444-555555555555","L":null,"R":60},{"I":"bad","L":3.0}],"New":1}"""
		)
		films.size shouldBe 2
		films[java.util.UUID.fromString("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")] shouldBe PickyfinScores.Scores(4.4, 91)
		films[java.util.UUID.fromString("11111111-2222-3333-4444-555555555555")] shouldBe PickyfinScores.Scores(null, 60)
	}

	test("Updater.versionCodeOf() matches the build's version codes") {
		Updater.versionCodeOf("0.19.10-pickyfin.1") shouldBe 191001
		Updater.versionCodeOf("0.19.10-pickyfin.12") shouldBe 191012
		Updater.versionCodeOf("0.20.0-pickyfin.1") shouldBe 200001
		Updater.versionCodeOf("0.19.10") shouldBe 191099
		Updater.versionCodeOf("not a version") shouldBe 0
	}
})
