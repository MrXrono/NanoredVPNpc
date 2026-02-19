package com.nanored.vpn.ui

import android.content.Intent
import android.net.Uri
import android.net.VpnService
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.view.KeyEvent
import android.view.Menu
import android.view.MenuItem
import android.view.View
import android.net.TrafficStats
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.appcompat.app.ActionBarDrawerToggle
import androidx.core.content.ContextCompat
import androidx.appcompat.app.AlertDialog
import androidx.core.view.GravityCompat
import androidx.core.view.isVisible
import androidx.lifecycle.lifecycleScope
import com.google.android.material.navigation.NavigationView
import com.google.android.material.tabs.TabLayoutMediator
import com.nanored.vpn.AppConfig
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivityMainBinding
import com.nanored.vpn.enums.EConfigType
import com.nanored.vpn.extension.toast
import com.nanored.vpn.extension.toastError
import com.nanored.vpn.handler.AngConfigManager
import com.nanored.vpn.handler.MmkvManager
import com.nanored.vpn.handler.SettingsChangeManager
import com.nanored.vpn.handler.SettingsManager
import com.nanored.vpn.handler.UpdateCheckerManager
import com.nanored.vpn.handler.V2RayServiceManager
import com.nanored.vpn.support.SupportChatApi
import com.nanored.vpn.support.SupportChatNotifier
import com.nanored.vpn.telemetry.NanoredTelemetry
import com.nanored.vpn.util.Utils
import com.nanored.vpn.viewmodel.MainViewModel
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : HelperBaseActivity(), NavigationView.OnNavigationItemSelectedListener {
    private val binding by lazy {
        ActivityMainBinding.inflate(layoutInflater)
    }

    val mainViewModel: MainViewModel by viewModels()
    private lateinit var groupPagerAdapter: GroupPagerAdapter
    private var tabMediator: TabLayoutMediator? = null
    private var fabTimeoutJob: Job? = null
    private var sessionTimerJob: Job? = null
    private var sessionStartTime: Long = 0L
    private var sessionStartRxBytes: Long = 0L
    private var lastRxBytes: Long = 0L
    private var lastIpInfoLine: String? = null
    private var lastPingMs: String? = null
    private lateinit var vpnButtonAnimator: VpnButtonAnimator
    private var isSwitchingCountry = false
    private var supportUnreadPollingJob: Job? = null
    private var lastSupportUnread = 0
    private var lastSupportUnreadRendered = 0
    private var supportChatToolbarButton: TextView? = null
    private var supportUnreadToolbarBadge: TextView? = null

    private val requestVpnPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (it.resultCode == RESULT_OK) {
            startV2Ray()
        }
    }
    private val requestInstallPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (UpdateCheckerManager.canInstallApk(this)) {
            UpdateCheckerManager.installPendingApk(this)
        } else {
            toastError("Разрешение на установку не получено")
            UpdateCheckerManager.clearPendingApk()
        }
    }
    private val requestSetupInstallPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        // Nothing extra needed — permission was requested during onboarding
    }
    private val multiPermissionLauncher = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { results ->
        // After runtime permissions, chain the install-packages request
        PermissionRationaleHelper.requestInstallIfNeeded(this, requestSetupInstallPermission)
    }
    private val requestActivityLauncher = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (SettingsChangeManager.consumeRestartService() && mainViewModel.isRunning.value == true) {
            restartV2Ray()
        }
        if (SettingsChangeManager.consumeSetupGroupTab()) {
            setupGroupTab()
        }
    }


    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(binding.root)
        setupToolbar(binding.toolbar, false, "Nanored VPN")

        // setup viewpager and tablayout
        groupPagerAdapter = GroupPagerAdapter(this, emptyList())
        binding.viewPager.adapter = groupPagerAdapter
        binding.viewPager.isUserInputEnabled = true

        // setup navigation drawer
        val toggle = ActionBarDrawerToggle(
            this, binding.drawerLayout, binding.toolbar, R.string.navigation_drawer_open, R.string.navigation_drawer_close
        )
        binding.drawerLayout.addDrawerListener(toggle)
        toggle.syncState()
        binding.navView.setNavigationItemSelectedListener(this)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (binding.drawerLayout.isDrawerOpen(GravityCompat.START)) {
                    binding.drawerLayout.closeDrawer(GravityCompat.START)
                } else {
                    isEnabled = false
                    onBackPressedDispatcher.onBackPressed()
                    isEnabled = true
                }
            }
        })

        // Initialize VPN button animator
        val btnContainer = binding.root.findViewById<FrameLayout>(R.id.vpn_button_container)
        val btnIcon = binding.root.findViewById<ImageView>(R.id.vpn_button_icon)
        val btnContent = binding.root.findViewById<LinearLayout>(R.id.vpn_button_content)
        val btnTime = binding.root.findViewById<TextView>(R.id.vpn_btn_time)
        val btnSpeed = binding.root.findViewById<TextView>(R.id.vpn_btn_speed)
        val btnPing = binding.root.findViewById<TextView>(R.id.vpn_btn_ping)
        vpnButtonAnimator = VpnButtonAnimator(btnContainer, btnIcon, btnContent, btnTime, btnSpeed, btnPing)
        vpnButtonAnimator.initialize()

        btnContainer.setOnClickListener { handleFabAction() }
        binding.layoutTest.setOnClickListener { handleLayoutTestClick() }
        binding.btnPingAll.setOnClickListener {
            toast("Тестирование пинга...")
            mainViewModel.testAllCountryPings()
            mainViewModel.testAllTcping()
        }

        setupGroupTab()
        setupViewModel()
        mainViewModel.reloadServerList()
        loadSubscriptionInfo()

        // Auto-ping countries on app startup
        mainViewModel.testAllCountryPings()

        PermissionRationaleHelper.showIfNeeded(this, multiPermissionLauncher, requestSetupInstallPermission)

        checkForAppUpdate()
        SupportChatNotifier.ensureChannel(this)
    }

    private fun checkForAppUpdate() {
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val result = UpdateCheckerManager.checkForUpdate()
                if (result.hasUpdate && !result.downloadUrl.isNullOrEmpty()) {
                    withContext(Dispatchers.Main) {
                        AlertDialog.Builder(this@MainActivity)
                            .setTitle("Доступно обновление v${result.latestVersion}")
                            .setMessage(result.releaseNotes ?: "Доступна новая версия приложения")
                            .setPositiveButton("Обновить") { _, _ ->
                                downloadAndInstallUpdate(result.downloadUrl!!)
                            }
                            .setNegativeButton("Позже", null)
                            .show()
                    }
                }
            } catch (e: Exception) {
                Log.e(AppConfig.TAG, "Update check failed", e)
            }
        }
    }

    private fun downloadAndInstallUpdate(downloadUrl: String) {
        toast("Загрузка обновления...")
        showLoading()
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val apkFile = UpdateCheckerManager.downloadApk(this@MainActivity, downloadUrl)
                withContext(Dispatchers.Main) {
                    hideLoading()
                    if (apkFile != null) {
                        tryInstallApk(apkFile)
                    } else {
                        toastError("Ошибка загрузки обновления")
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    hideLoading()
                    toastError("Ошибка: ${e.message}")
                }
            }
        }
    }

    private fun tryInstallApk(apkFile: java.io.File) {
        if (UpdateCheckerManager.canInstallApk(this)) {
            UpdateCheckerManager.installApk(this, apkFile)
        } else {
            // Save pending APK, then request permission
            UpdateCheckerManager.savePendingApk(apkFile)
            toast("Разрешите установку из этого источника")
            requestInstallPermission.launch(UpdateCheckerManager.getInstallPermissionIntent(this))
        }
    }

    private fun setupViewModel() {
        mainViewModel.updateTestResultAction.observe(this) { content ->
            if (content != null && content.contains("\n")) {
                // Format: "Соединение успешно: заняло XX мс\n{flag}(CC) IP"
                val parts = content.split("\n", limit = 2)
                lastIpInfoLine = parts[1]
                // Extract ping from first line, e.g. "Соединение успешно: заняло 123 мс"
                val pingMatch = Regex("(\\d+)\\s*мс").find(parts[0])
                lastPingMs = pingMatch?.groupValues?.get(1)
                updateSessionStatusText(parts[0])
            } else {
                setTestState(content)
            }
        }
        mainViewModel.isRunning.observe(this) { isRunning ->
            fabTimeoutJob?.cancel()
            fabTimeoutJob = null
            // Skip state update during country switch (VPN stops temporarily)
            if (isSwitchingCountry && !isRunning) return@observe
            applyRunningState(false, isRunning)
        }
        mainViewModel.startListenBroadcast()
        mainViewModel.initAssets(assets)
    }

    private fun setupGroupTab() {
        val groups = mainViewModel.getSubscriptions(this)
        groupPagerAdapter.update(groups)

        tabMediator?.detach()
        tabMediator = TabLayoutMediator(binding.tabGroup, binding.viewPager) { tab, position ->
            groupPagerAdapter.groups.getOrNull(position)?.let {
                tab.text = it.remarks
                tab.tag = it.id
            }
        }.also { it.attach() }

        val targetIndex = groups.indexOfFirst { it.id == mainViewModel.subscriptionId }.takeIf { it >= 0 } ?: (groups.size - 1)
        binding.viewPager.setCurrentItem(targetIndex, false)

        binding.tabGroup.isVisible = groups.size > 1
    }

    private fun handleFabAction() {
        applyRunningState(isLoading = true, isRunning = false)

        fabTimeoutJob?.cancel()
        fabTimeoutJob = lifecycleScope.launch {
            delay(10_000L)
            applyRunningState(false, mainViewModel.isRunning.value == true)
        }

        if (mainViewModel.isRunning.value == true) {
            V2RayServiceManager.stopVService(this)
        } else if (mainViewModel.selectedCountryCode != null) {
            connectViaBestServer()
        } else if (SettingsManager.isVpnMode()) {
            val intent = VpnService.prepare(this)
            if (intent == null) {
                startV2Ray()
            } else {
                requestVpnPermission.launch(intent)
            }
        } else {
            startV2Ray()
        }
    }

    private fun connectViaBestServer() {
        val code = mainViewModel.selectedCountryCode ?: return
        setTestState("Поиск лучшего сервера...")
        mainViewModel.findBestServerForCountry(code) { guid ->
            if (guid != null) {
                MmkvManager.setSelectServer(guid)
                val profile = MmkvManager.decodeServerConfig(guid)
                setTestState("Подключение: ${profile?.remarks.orEmpty()}")
                if (SettingsManager.isVpnMode()) {
                    val intent = VpnService.prepare(this)
                    if (intent == null) {
                        startV2Ray()
                    } else {
                        requestVpnPermission.launch(intent)
                    }
                } else {
                    startV2Ray()
                }
            } else {
                toast("Нет доступных серверов для этой страны")
                applyRunningState(false, false)
            }
        }
    }

    fun switchCountry(code: String) {
        isSwitchingCountry = true
        setTestState("Переключение страны...")
        // Don't collapse the button — stay expanded, just flash to connecting color
        vpnButtonAnimator.flashToConnecting()
        V2RayServiceManager.stopVService(this)
        lifecycleScope.launch {
            delay(600)
            mainViewModel.selectedCountryCode = code
            setTestState("Поиск лучшего сервера...")
            isSwitchingCountry = false
            connectViaBestServer()
        }
    }

    private fun handleLayoutTestClick() {
        if (mainViewModel.isRunning.value == true) {
            setTestState(getString(R.string.connection_test_testing))
            mainViewModel.testCurrentServerRealPing()
        } else {
            // service not running: keep existing no-op (could show a message if desired)
        }
    }

    private fun startV2Ray() {
        if (MmkvManager.getSelectServer().isNullOrEmpty()) {
            toast(R.string.title_file_chooser)
            return
        }
        V2RayServiceManager.startVService(this)
    }

    fun restartV2Ray() {
        if (mainViewModel.isRunning.value == true) {
            V2RayServiceManager.stopVService(this)
        }
        lifecycleScope.launch {
            delay(500)
            startV2Ray()
        }
    }

    private fun setTestState(content: String?) {
        binding.tvTestState.text = content
    }

    private fun applyRunningState(isLoading: Boolean, isRunning: Boolean) {
        if (isLoading) {
            // Determine target state based on current animator state
            val animState = vpnButtonAnimator.currentState
            if (animState == VpnButtonAnimator.State.IDLE || animState == VpnButtonAnimator.State.ERROR) {
                vpnButtonAnimator.resetSessionInfo()
                vpnButtonAnimator.transitionTo(VpnButtonAnimator.State.CONNECTING)
                setTestState("Подключение...")
            } else if (animState == VpnButtonAnimator.State.CONNECTED) {
                vpnButtonAnimator.transitionTo(VpnButtonAnimator.State.DISCONNECTING)
                setTestState("Отключение...")
            }
            return
        }

        if (isRunning) {
            vpnButtonAnimator.transitionTo(VpnButtonAnimator.State.CONNECTED)
            setTestState(getString(R.string.connection_connected))
            binding.layoutTest.isFocusable = true
            startSessionTimer()
        } else {
            val prevState = vpnButtonAnimator.currentState
            if (prevState == VpnButtonAnimator.State.CONNECTING) {
                // Connection failed — show error
                vpnButtonAnimator.transitionTo(VpnButtonAnimator.State.ERROR)
                setTestState("Ошибка подключения")
            } else if (prevState == VpnButtonAnimator.State.DISCONNECTING) {
                // Let DISCONNECTING animation finish naturally and switch to IDLE itself.
            } else {
                vpnButtonAnimator.transitionTo(VpnButtonAnimator.State.IDLE)
            }
            setTestState(getString(R.string.connection_not_connected))
            binding.layoutTest.isFocusable = false
            stopSessionTimer()
        }
    }

    private fun startSessionTimer() {
        sessionStartTime = SystemClock.elapsedRealtime()
        sessionStartRxBytes = TrafficStats.getTotalRxBytes()
        lastRxBytes = sessionStartRxBytes
        lastIpInfoLine = null
        lastPingMs = null
        vpnButtonAnimator.resetSessionInfo()
        updateButtonContent()
        sessionTimerJob?.cancel()
        sessionTimerJob = lifecycleScope.launch {
            while (true) {
                delay(1000L)
                updateButtonContent()
                if (lastIpInfoLine != null) {
                    updateSessionStatusText(null)
                }
            }
        }
    }

    private fun stopSessionTimer() {
        sessionTimerJob?.cancel()
        sessionTimerJob = null
        lastIpInfoLine = null
    }

    private fun updateSessionStatusText(firstLine: String?) {
        val ipLine = lastIpInfoLine ?: return
        // Extract flag + IPv4 only from ip line like "🇳🇱(nl) 185.204.1.42"
        val statusLine = extractFlagAndIpv4(ipLine)
        val statusText = if (firstLine != null) "$firstLine\n$statusLine" else statusLine
        setTestState(statusText)
    }

    private fun updateButtonContent() {
        val elapsed = (SystemClock.elapsedRealtime() - sessionStartTime) / 1000
        val timeStr = formatSessionTime(elapsed)

        val currentRx = TrafficStats.getTotalRxBytes()
        val speedBytes = currentRx - lastRxBytes
        lastRxBytes = currentRx
        val speedStr = "\u2193 ${formatSpeed(speedBytes)}/s"

        val pingStr = lastPingMs?.let { "${it} ms" } ?: ""

        vpnButtonAnimator.updateSessionInfo(timeStr, speedStr, pingStr)
    }

    private fun extractFlagAndIpv4(ipLine: String): String {
        // ipLine format: "🇳🇱(nl) 185.204.1.42" or "🇳🇱(nl) 2a01:xxx 185.204.1.42"
        // Extract flag (emoji at start) and IPv4 address
        val ipv4Regex = Regex("(\\d{1,3}\\.\\d{1,3}\\.\\d{1,3}\\.\\d{1,3})")
        val ipv4Match = ipv4Regex.find(ipLine)
        val ipv4 = ipv4Match?.value ?: ""

        // Extract flag: everything before "("
        val flagEnd = ipLine.indexOf('(')
        val flag = if (flagEnd > 0) ipLine.substring(0, flagEnd).trim() else ""

        return if (flag.isNotEmpty() && ipv4.isNotEmpty()) "$flag $ipv4"
        else if (ipv4.isNotEmpty()) ipv4
        else ipLine
    }

    private fun formatSessionTime(totalSeconds: Long): String {
        val hours = totalSeconds / 3600
        val minutes = (totalSeconds % 3600) / 60
        val seconds = totalSeconds % 60
        return if (hours > 0) {
            String.format("%d:%02d:%02d", hours, minutes, seconds)
        } else {
            String.format("%02d:%02d", minutes, seconds)
        }
    }

    private fun formatBytes(bytes: Long): String {
        return when {
            bytes < 1024 -> "$bytes B"
            bytes < 1024 * 1024 -> String.format("%.1f KB", bytes / 1024.0)
            bytes < 1024 * 1024 * 1024 -> String.format("%.2f MB", bytes / (1024.0 * 1024.0))
            else -> String.format("%.2f GB", bytes / (1024.0 * 1024.0 * 1024.0))
        }
    }

    private fun formatSpeed(bytesPerSec: Long): String {
        return when {
            bytesPerSec < 1024 -> "$bytesPerSec B"
            bytesPerSec < 1024 * 1024 -> String.format("%.1f KB", bytesPerSec / 1024.0)
            else -> String.format("%.1f MB", bytesPerSec / (1024.0 * 1024.0))
        }
    }

    private fun loadSubscriptionInfo() {
        val subs = MmkvManager.decodeSubscriptions()
        val sub = subs.firstOrNull()?.subscription
        if (sub != null && (sub.accountId != null || sub.downloadBytes > 0 || sub.expireTimestamp != 0L)) {
            displaySubscriptionInfo(sub)
            // Sync account_id from subscription to telemetry prefs
            com.nanored.vpn.telemetry.NanoredTelemetry.syncAccountId(sub.accountId)
        }
    }

    private fun displaySubscriptionInfo(sub: com.nanored.vpn.dto.SubscriptionItem) {
        binding.cardSubInfo.visibility = View.VISIBLE

        val accountText = if (!sub.accountId.isNullOrEmpty()) {
            "Аккаунт: ${sub.accountId}"
        } else {
            ""
        }
        binding.tvSubAccount.text = accountText

        val isActive = sub.expireTimestamp == 0L || sub.expireTimestamp > System.currentTimeMillis() / 1000
        binding.tvSubStatus.text = if (isActive) "Активен" else "Истек"
        binding.tvSubStatus.setTextColor(
            ContextCompat.getColor(this, if (isActive) R.color.color_fab_active else R.color.color_fab_inactive)
        )

        val dlGb = sub.downloadBytes / (1024.0 * 1024.0 * 1024.0)
        val dlText = if (sub.totalBytes > 0) {
            val totalGb = sub.totalBytes / (1024.0 * 1024.0 * 1024.0)
            String.format("%.2f / %.1f GB", dlGb, totalGb)
        } else {
            String.format("%.2f GB", dlGb)
        }
        binding.tvSubDownload.text = "Скачено: $dlText"

        val expireText = when {
            sub.expireTimestamp == 0L -> "Бессрочно"
            else -> {
                val now = System.currentTimeMillis() / 1000
                val diff = sub.expireTimestamp - now
                when {
                    diff <= 0 -> "Истек"
                    diff < 3600 -> "${diff / 60} мин."
                    diff < 86400 -> "${diff / 3600} ч."
                    else -> "${diff / 86400} дн."
                }
            }
        }
        binding.tvSubExpire.text = "Время подписки: $expireText"
    }

    override fun onResume() {
        super.onResume()
        startSupportUnreadPolling()
    }

    override fun onPause() {
        stopSupportUnreadPolling()
        super.onPause()
    }

    private fun startSupportUnreadPolling() {
        if (supportUnreadPollingJob != null) return
        supportUnreadPollingJob = lifecycleScope.launch(Dispatchers.IO) {
            while (true) {
                try {
                    val unread = SupportChatApi.unreadCount(this@MainActivity)
                    withContext(Dispatchers.Main) { renderSupportUnread(unread) }
                } catch (_: Exception) {
                }
                delay(10_000)
            }
        }
    }

    private fun stopSupportUnreadPolling() {
        supportUnreadPollingJob?.cancel()
        supportUnreadPollingJob = null
    }

    private fun renderSupportUnread(unread: Int) {
        // Keep the latest unread around even if the actionView hasn't been inflated yet.
        lastSupportUnread = unread
        supportUnreadToolbarBadge?.isVisible = unread > 0
        if (unread > 0) {
            supportUnreadToolbarBadge?.text = if (unread > 99) "99+" else unread.toString()
            // lastSupportUnread is persisted above; compare vs previous rendered value.
            if (unread > lastSupportUnreadRendered) {
                SupportChatNotifier.notifyNewMessage(
                    this,
                    "Новых сообщений: ${unread - lastSupportUnreadRendered}",
                )
            }
        }
        lastSupportUnreadRendered = unread
    }

    override fun onCreateOptionsMenu(menu: Menu): Boolean {
        menuInflater.inflate(R.menu.menu_main, menu)
        val supportItem = menu.findItem(R.id.support_chat_toolbar)
        val actionView = supportItem?.actionView
        supportChatToolbarButton = actionView?.findViewById(R.id.btn_support_chat_toolbar)
        supportUnreadToolbarBadge = actionView?.findViewById(R.id.tv_support_unread_badge_toolbar)
        supportChatToolbarButton?.setOnClickListener {
            startActivity(Intent(this, SupportChatActivity::class.java))
        }
        // Render immediately using the latest unread value gathered by polling.
        renderSupportUnread(lastSupportUnread)
        return super.onCreateOptionsMenu(menu)
    }

    override fun onOptionsItemSelected(item: MenuItem) = when (item.itemId) {
        R.id.support_chat_toolbar -> {
            startActivity(Intent(this, SupportChatActivity::class.java))
            true
        }

        R.id.ping_all -> {
            toast(getString(R.string.connection_test_testing_count, mainViewModel.serversCache.count()))
            mainViewModel.testAllTcping()
            true
        }

        R.id.real_ping_all -> {
            toast(getString(R.string.connection_test_testing_count, mainViewModel.serversCache.count()))
            mainViewModel.testAllRealPing()
            true
        }

        R.id.service_restart -> {
            restartV2Ray()
            true
        }

        R.id.sort_by_test_results -> {
            sortByTestResults()
            true
        }

        R.id.sub_update -> {
            importConfigViaSub()
            true
        }

        else -> super.onOptionsItemSelected(item)
    }

    private fun importManually(createConfigType: Int) {
        if (createConfigType == EConfigType.POLICYGROUP.value) {
            startActivity(
                Intent()
                    .putExtra("subscriptionId", mainViewModel.subscriptionId)
                    .setClass(this, ServerGroupActivity::class.java)
            )
        } else {
            startActivity(
                Intent()
                    .putExtra("createConfigType", createConfigType)
                    .putExtra("subscriptionId", mainViewModel.subscriptionId)
                    .setClass(this, ServerActivity::class.java)
            )
        }
    }

    /**
     * import config from qrcode
     */
    private fun importQRcode(): Boolean {
        launchQRCodeScanner { scanResult ->
            if (scanResult != null) {
                importBatchConfig(scanResult)
            }
        }
        return true
    }

    /**
     * import config from clipboard
     */
    private fun importClipboard()
            : Boolean {
        try {
            val clipboard = Utils.getClipboard(this)
            importBatchConfig(clipboard)
        } catch (e: Exception) {
            Log.e(AppConfig.TAG, "Failed to import config from clipboard", e)
            return false
        }
        return true
    }

    private fun importBatchConfig(server: String?) {
        showLoading()

        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val (count, countSub) = AngConfigManager.importBatchConfig(server, mainViewModel.subscriptionId, true)
                delay(500L)
                withContext(Dispatchers.Main) {
                    when {
                        count > 0 -> {
                            toast(getString(R.string.title_import_config_count, count))
                            mainViewModel.reloadServerList()
                        }

                        countSub > 0 -> setupGroupTab()
                        else -> toastError(R.string.toast_failure)
                    }
                    hideLoading()
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    toastError(R.string.toast_failure)
                    hideLoading()
                }
                Log.e(AppConfig.TAG, "Failed to import batch config", e)
            }
        }
    }

    /**
     * import config from local config file
     */
    private fun importConfigLocal(): Boolean {
        try {
            showFileChooser()
        } catch (e: Exception) {
            Log.e(AppConfig.TAG, "Failed to import config from local file", e)
            return false
        }
        return true
    }


    /**
     * import config from sub
     */
    private fun importConfigViaSub(): Boolean {
        showLoading()

        lifecycleScope.launch(Dispatchers.IO) {
            val count = mainViewModel.updateConfigViaSubAll()
            delay(500L)
            launch(Dispatchers.Main) {
                if (count > 0) {
                    toast(getString(R.string.title_update_config_count, count))
                    mainViewModel.reloadServerList()
                    loadSubscriptionInfo()
                } else {
                    toastError(R.string.toast_failure)
                }
                hideLoading()
            }
        }
        return true
    }

    private fun exportAll() {
        showLoading()
        lifecycleScope.launch(Dispatchers.IO) {
            val ret = mainViewModel.exportAllServer()
            launch(Dispatchers.Main) {
                if (ret > 0)
                    toast(getString(R.string.title_export_config_count, ret))
                else
                    toastError(R.string.toast_failure)
                hideLoading()
            }
        }
    }

    private fun delAllConfig() {
        AlertDialog.Builder(this).setMessage(R.string.del_config_comfirm)
            .setPositiveButton(android.R.string.ok) { _, _ ->
                showLoading()
                lifecycleScope.launch(Dispatchers.IO) {
                    val ret = mainViewModel.removeAllServer()
                    launch(Dispatchers.Main) {
                        mainViewModel.reloadServerList()
                        toast(getString(R.string.title_del_config_count, ret))
                        hideLoading()
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel) { _, _ ->
                //do noting
            }
            .show()
    }

    private fun delDuplicateConfig() {
        AlertDialog.Builder(this).setMessage(R.string.del_config_comfirm)
            .setPositiveButton(android.R.string.ok) { _, _ ->
                showLoading()
                lifecycleScope.launch(Dispatchers.IO) {
                    val ret = mainViewModel.removeDuplicateServer()
                    launch(Dispatchers.Main) {
                        mainViewModel.reloadServerList()
                        toast(getString(R.string.title_del_duplicate_config_count, ret))
                        hideLoading()
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel) { _, _ ->
                //do noting
            }
            .show()
    }

    private fun delInvalidConfig() {
        AlertDialog.Builder(this).setMessage(R.string.del_invalid_config_comfirm)
            .setPositiveButton(android.R.string.ok) { _, _ ->
                showLoading()
                lifecycleScope.launch(Dispatchers.IO) {
                    val ret = mainViewModel.removeInvalidServer()
                    launch(Dispatchers.Main) {
                        mainViewModel.reloadServerList()
                        toast(getString(R.string.title_del_config_count, ret))
                        hideLoading()
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel) { _, _ ->
                //do noting
            }
            .show()
    }

    private fun sortByTestResults() {
        showLoading()
        lifecycleScope.launch(Dispatchers.IO) {
            mainViewModel.sortByTestResults()
            launch(Dispatchers.Main) {
                mainViewModel.reloadServerList()
                hideLoading()
            }
        }
    }

    /**
     * show file chooser
     */
    private fun showFileChooser() {
        launchFileChooser { uri ->
            if (uri == null) {
                return@launchFileChooser
            }

            readContentFromUri(uri)
        }
    }

    /**
     * read content from uri
     */
    private fun readContentFromUri(uri: Uri) {
        try {
            contentResolver.openInputStream(uri).use { input ->
                importBatchConfig(input?.bufferedReader()?.readText())
            }
        } catch (e: Exception) {
            Log.e(AppConfig.TAG, "Failed to read content from URI", e)
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK || keyCode == KeyEvent.KEYCODE_BUTTON_B) {
            moveTaskToBack(false)
            return true
        }
        return super.onKeyDown(keyCode, event)
    }


    override fun onNavigationItemSelected(item: MenuItem): Boolean {
        when (item.itemId) {
            R.id.per_app_proxy_settings -> requestActivityLauncher.launch(Intent(this, PerAppProxyActivity::class.java))
            R.id.routing_setting -> requestActivityLauncher.launch(Intent(this, RoutingSettingActivity::class.java))
            R.id.user_asset_setting -> requestActivityLauncher.launch(Intent(this, UserAssetActivity::class.java))
            R.id.settings -> requestActivityLauncher.launch(Intent(this, SettingsActivity::class.java))
            R.id.logcat -> startActivity(Intent(this, LogcatActivity::class.java))
            R.id.send_logs -> sendFullLog()
            R.id.backup_restore -> requestActivityLauncher.launch(Intent(this, BackupActivity::class.java))
            R.id.check_update -> startActivity(Intent(this, CheckUpdateActivity::class.java))
            R.id.about -> startActivity(Intent(this, AboutActivity::class.java))
            R.id.logout -> {
                AlertDialog.Builder(this)
                    .setMessage("Выйти из аккаунта? Все настройки подписки будут удалены.")
                    .setPositiveButton(android.R.string.ok) { _, _ ->
                        if (mainViewModel.isRunning.value == true) {
                            V2RayServiceManager.stopVService(this)
                        }
                        lifecycleScope.launch(Dispatchers.IO) {
                            MmkvManager.decodeSubscriptions().forEach {
                                MmkvManager.removeSubscription(it.guid)
                            }
                            MmkvManager.removeAllServer()
                            withContext(Dispatchers.Main) {
                                startActivity(Intent(this@MainActivity, SetupActivity::class.java).apply {
                                    flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
                                })
                                finish()
                            }
                        }
                    }
                    .setNegativeButton(android.R.string.cancel, null)
                    .show()
            }
        }

        binding.drawerLayout.closeDrawer(GravityCompat.START)
        return true
    }

    private fun sendFullLog() {
        toast(R.string.send_logs_sending)
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val sb = StringBuilder()

                // 1. App logcat
                sb.appendLine("=== LOGCAT ===")
                try {
                    val process = Runtime.getRuntime().exec(arrayOf("logcat", "-d"))
                    val logcat = process.inputStream.bufferedReader().use { it.readText() }
                    process.waitFor()
                    sb.appendLine(logcat)
                } catch (e: Exception) {
                    sb.appendLine("Logcat error: ${e.message}")
                }

                // 2. Xray access log
                sb.appendLine("=== XRAY ACCESS LOG ===")
                val accessLog = java.io.File(filesDir, "v2ray_access.log")
                if (accessLog.exists()) {
                    sb.appendLine(accessLog.readText())
                } else {
                    sb.appendLine("(no access log)")
                }

                // 3. Xray error log
                sb.appendLine("=== XRAY ERROR LOG ===")
                val errorLog = java.io.File(filesDir, "v2ray_error.log")
                if (errorLog.exists()) {
                    sb.appendLine(errorLog.readText())
                } else {
                    sb.appendLine("(no error log)")
                }

                NanoredTelemetry.sendDeviceLog("full_log", sb.toString().replace("\u0000", ""))

                withContext(Dispatchers.Main) {
                    toast(R.string.send_logs_success)
                }
            } catch (e: Exception) {
                Log.e(AppConfig.TAG, "Send full log failed", e)
                withContext(Dispatchers.Main) {
                    toastError(R.string.toast_failure)
                }
            }
        }
    }

    override fun onDestroy() {
        stopSupportUnreadPolling()
        vpnButtonAnimator.destroy()
        sessionTimerJob?.cancel()
        tabMediator?.detach()
        super.onDestroy()
    }
}
