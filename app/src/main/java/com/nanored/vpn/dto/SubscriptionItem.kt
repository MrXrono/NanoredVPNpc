package com.nanored.vpn.dto

data class SubscriptionItem(
    var remarks: String = "",
    var url: String = "",
    var enabled: Boolean = true,
    val addedTime: Long = System.currentTimeMillis(),
    var lastUpdated: Long = -1,
    var autoUpdate: Boolean = false,
    val updateInterval: Int? = null,
    var prevProfile: String? = null,
    var nextProfile: String? = null,
    var filter: String? = null,
    var allowInsecureUrl: Boolean = false,
    var userAgent: String? = null,
    var accountId: String? = null,
    var downloadBytes: Long = 0,
    var uploadBytes: Long = 0,
    var totalBytes: Long = 0,
    var expireTimestamp: Long = 0,
)

