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

	test("Updater.versionCodeOf() matches the build's version codes") {
		Updater.versionCodeOf("0.19.10-pickyfin.1") shouldBe 191001
		Updater.versionCodeOf("0.19.10-pickyfin.12") shouldBe 191012
		Updater.versionCodeOf("0.20.0-pickyfin.1") shouldBe 200001
		Updater.versionCodeOf("0.19.10") shouldBe 191099
		Updater.versionCodeOf("not a version") shouldBe 0
	}
})
