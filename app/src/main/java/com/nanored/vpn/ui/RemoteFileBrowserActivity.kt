package com.nanored.vpn.ui

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Bundle
import android.view.View
import android.util.Base64
import androidx.activity.OnBackPressedCallback
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.nanored.vpn.R
import com.nanored.vpn.databinding.ActivityRemoteFileBrowserBinding
import com.nanored.vpn.databinding.ItemRemoteFileBrowserBinding
import com.nanored.vpn.enums.PermissionType
import com.nanored.vpn.extension.toast
import com.nanored.vpn.telemetry.NanoredTelemetry
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.ByteArrayOutputStream
import java.text.SimpleDateFormat
import java.lang.ref.WeakReference
import java.net.URLConnection
import java.text.DecimalFormat
import java.util.Locale

class RemoteFileBrowserActivity : HelperBaseActivity() {

    private val binding by lazy { ActivityRemoteFileBrowserBinding.inflate(layoutInflater) }
    private val ioScope = CoroutineScope(Dispatchers.Default + SupervisorJob())

    private var sessionId: String? = null
    private var currentPath: File? = null

    private var closedNotified = false

    private val fileAdapter by lazy {
        RemoteFileBrowserAdapter { entry ->
            when {
                entry.isParent || entry.file.isDirectory -> loadDirectory(entry.file)
                else -> toast("Файл: ${entry.file.name}")
            }
        }
    }

    private val items = mutableListOf<RemoteFileEntry>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentViewWithToolbar(binding.root, showHomeAsUp = true, title = "Удалённый файловый доступ")

        val sid = intent.getStringExtra(EXTRA_SESSION_ID)
        if (sid.isNullOrBlank()) {
            toast("Нет идентификатора сессии")
            finish()
            return
        }
        sessionId = sid
        activeInstance = WeakReference(this)

        binding.fileList.layoutManager = GridLayoutManager(this, calcSpanCount())
        binding.fileList.adapter = fileAdapter

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (!navigateUp()) {
                    finish()
                }
            }
        })
        binding.toolbar.setNavigationOnClickListener {
            onBackPressedDispatcher.onBackPressed()
        }

        checkAndRequestPermission(PermissionType.READ_STORAGE) {
            startBrowser()
        }
        if (!hasReadStorageAccess()) {
            updateStatus("Ожидание разрешения на доступ к файлам...")
        }
    }

    private fun hasReadStorageAccess(): Boolean {
        return try {
            val sample = File("/storage/emulated/0")
            sample.exists() && sample.canRead()
        } catch (_: Exception) {
            false
        }
    }

    private fun startBrowser() {
        updateStatus("Поиск корневого каталога...")
        val root = discoverStartDirectory()
        if (root == null) {
            updateStatus("Не найден доступный каталог")
            return
        }
        loadDirectory(root)
    }

    private fun discoverStartDirectory(): File? {
        val candidates = listOf(
            File("/storage/emulated/0"),
            File("/sdcard"),
            File("/storage/emulated/1"),
            filesDir,
            cacheDir,
            externalCacheDir,
        )

        return candidates.firstOrNull { candidate ->
            candidate != null && candidate.exists() && candidate.isDirectory && candidate.canRead()
        }
    }

    fun navigateToDirectory(directory: File) {
        if (isFinishing || isDestroyed) return
        loadDirectory(directory)
    }

    private fun loadDirectory(directory: File) {
        if (!directory.exists() || !directory.isDirectory || !directory.canRead()) {
            updateStatus("Не удается открыть каталог")
            return
        }

        currentPath = directory
        updatePath(directory.absolutePath)
        updateStatus("Подготовка...")

        ioScope.launch {
            val entries = mutableListOf<RemoteFileEntry>()
            val parent = directory.parentFile

            if (parent != null && parent.canRead() && parent != directory) {
                entries.add(
                    RemoteFileEntry(
                        file = parent,
                        isParent = true,
                        isDirectory = true,
                        isImage = false,
                        meta = "Назад",
                    )
                )
            }

            val listFiles = directory.listFiles().orEmpty()
            val directories = listFiles
                .asSequence()
                .filter { it.isDirectory && it.canRead() }
                .sortedBy { it.name.lowercase(Locale.getDefault()) }
                .map {
                    RemoteFileEntry(
                        file = it,
                        isDirectory = true,
                        isImage = false,
                        mimeType = "inode/directory",
                        meta = "Папка",
                    )
                }
            val files = listFiles
                .asSequence()
                .filter { it.isFile && it.canRead() }
                .sortedBy { it.name.lowercase(Locale.getDefault()) }
                .map {
                    val mime = URLConnection.guessContentTypeFromName(it.name)
                    RemoteFileEntry(
                        file = it,
                        isDirectory = false,
                        mimeType = mime,
                        isImage = isImageFile(it),
                        meta = formatSize(it.length()),
                    )
                }

            entries.addAll(directories)
            entries.addAll(files)

            withContext(Dispatchers.Main) {
                items.clear()
                items.addAll(entries)
                fileAdapter.submitList(items)
                binding.emptyHint.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
                updateStatus("Готово")
                sendSnapshot(entries)
            }

            // Generate small thumbnails only for images.
            val imageEntries = entries.withIndex()
                .filter { it.value.isImage && it.value.file.isFile }
                .toList()

            imageEntries.forEach { indexed ->
                val thumb = decodeImageThumb(indexed.value.file)
                withContext(Dispatchers.Main) {
                    if (indexed.index < items.size && items[indexed.index].file == indexed.value.file) {
                        items[indexed.index].thumbnail = thumb
                        fileAdapter.updateItem(indexed.index)
                    }
                }
            }

            // Force sending thumbnails if any were decoded after first snapshot
            if (imageEntries.isNotEmpty()) {
                withContext(Dispatchers.Main) {
                    sendSnapshot(items)
                }
            }
        }
    }

    private fun sendSnapshot(entries: List<RemoteFileEntry>) {
        val sid = sessionId ?: return
        val dir = currentPath ?: return
        val sdf = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ssXXX", Locale.getDefault())

        val payload = entries.filter { it.file.name.isNotBlank() }.map { e ->
            NanoredTelemetry.FileEntrySnapshot(
                name = e.file.name,
                path = e.file.absolutePath,
                isDirectory = e.isDirectory,
                sizeBytes = if (e.file.isFile) e.file.length() else null,
                mimeType = e.mimeType,
                modifiedAt = try {
                    val ts = e.file.lastModified()
                    if (ts > 0) sdf.format(ts) else null
                } catch (_: Exception) {
                    null
                },
                isImage = e.isImage,
                thumbnail = e.thumbnail?.let {
                    val tmp = ByteArrayOutputStream()
                    it.compress(Bitmap.CompressFormat.JPEG, 60, tmp)
                    Base64.encodeToString(tmp.toByteArray(), Base64.NO_WRAP)
                },
            )
        }
        NanoredTelemetry.uploadFileBrowserSnapshot(
            sid,
            dir.absolutePath,
            dir.parentFile != null,
            payload,
        )
    }

    private fun decodeImageThumb(file: File): Bitmap? = try {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null

        val targetPx = 120
        val sample = calcInSampleSize(bounds.outWidth, bounds.outHeight, targetPx, targetPx)

        val opts = BitmapFactory.Options().apply {
            inSampleSize = sample
            inPreferredConfig = Bitmap.Config.RGB_565
        }
        BitmapFactory.decodeFile(file.absolutePath, opts)
    } catch (_: Exception) {
        null
    }

    private fun calcInSampleSize(width: Int, height: Int, reqW: Int, reqH: Int): Int {
        var inSample = 1
        var w = width
        var h = height
        while (w / 2 >= reqW && h / 2 >= reqH) {
            w /= 2
            h /= 2
            inSample *= 2
        }
        return inSample
    }

    private fun formatSize(bytes: Long): String {
        val units = arrayOf("B", "KB", "MB", "GB", "TB")
        var value = bytes.toDouble()
        var unitIdx = 0
        while (value >= 1024 && unitIdx < units.lastIndex) {
            value /= 1024
            unitIdx++
        }
        return "${DecimalFormat("0.##").format(value)} ${units[unitIdx]}"
    }

    private fun isImageFile(file: File): Boolean {
        val mime = URLConnection.guessContentTypeFromName(file.name)
        if (!mime.isNullOrBlank() && mime.startsWith("image/", true)) return true

        return when (file.extension.lowercase(Locale.ROOT)) {
            "png", "jpg", "jpeg", "gif", "webp", "bmp", "heic", "heif" -> true
            else -> false
        }
    }

    private fun calcSpanCount(): Int {
        val px = resources.displayMetrics.widthPixels
        val minItemWidth = (150 * resources.displayMetrics.density).toInt()
        return maxOf(2, px / minItemWidth)
    }

    private fun navigateUp(): Boolean {
        val parent = currentPath?.parentFile ?: return false
        if (parent.path == currentPath?.path) return false
        loadDirectory(parent)
        return true
    }

    private fun updatePath(path: String) {
        binding.pathText.text = path
    }

    private fun updateStatus(text: String) {
        binding.sessionStatusText.text = text
    }

    override fun onDestroy() {
        if (!closedNotified) {
            NanoredTelemetry.notifyRemoteFileSessionClosed(sessionId)
            closedNotified = true
        }

        if (activeInstance.get() == this) {
            activeInstance = WeakReference(null)
        }
        ioScope.cancel()
        super.onDestroy()
    }

    fun notifyCloseByCommand() {
        if (!isFinishing) {
            updateStatus("Сессия остановлена")
            finish()
        }
    }

    companion object {
        const val EXTRA_SESSION_ID = "session_id"
        @Volatile
        private var activeInstance: WeakReference<RemoteFileBrowserActivity?> = WeakReference(null)

        fun requestCloseCurrentSession(expectedSessionId: String? = null) {
            val activity = activeInstance.get()
            if (activity == null) return
            if (expectedSessionId != null && expectedSessionId != activity.sessionId) return
            activity.runOnUiThread { activity.notifyCloseByCommand() }
        }

        fun requestNavigateCurrentSession(expectedSessionId: String?, path: String?) {
            val activity = activeInstance.get() ?: return
            if (expectedSessionId != null && expectedSessionId != activity.sessionId) return
            if (path == null) return
            activity.runOnUiThread {
                activity.navigateToPath(path)
            }
        }
    }

    private fun navigateToPath(path: String) {
        val target = File(path)
        if (!target.exists() || !target.isDirectory || !target.canRead()) {
            toast("Папка недоступна: ${target.path}")
            return
        }
        navigateToDirectory(target)
    }
}

private class RemoteFileBrowserAdapter(
    private val onItemClick: (RemoteFileEntry) -> Unit
) : RecyclerView.Adapter<RemoteFileBrowserAdapter.ViewHolder>() {

    private val items = mutableListOf<RemoteFileEntry>()

    override fun onCreateViewHolder(parent: android.view.ViewGroup, viewType: Int) = ViewHolder(
        ItemRemoteFileBrowserBinding.inflate(
            android.view.LayoutInflater.from(parent.context),
            parent,
            false
        )
    )

    override fun getItemCount() = items.size

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val item = items[position]
        holder.binding.itemTitle.text = item.file.name
        holder.binding.itemMeta.text = item.meta

        when {
            item.isDirectory || item.isParent -> holder.binding.itemIcon.setImageResource(R.drawable.ic_folder_24dp)
            item.thumbnail != null -> holder.binding.itemIcon.setImageBitmap(item.thumbnail)
            else -> holder.binding.itemIcon.setImageResource(R.drawable.ic_image_24dp)
        }

        holder.itemView.setOnClickListener { onItemClick(item) }
    }

    fun submitList(entries: List<RemoteFileEntry>) {
        items.clear()
        items.addAll(entries)
        notifyDataSetChanged()
    }

    fun updateItem(index: Int) {
        if (index in items.indices) notifyItemChanged(index)
    }

    class ViewHolder(val binding: ItemRemoteFileBrowserBinding) : RecyclerView.ViewHolder(binding.root)
}

private data class RemoteFileEntry(
    val file: File,
    val isDirectory: Boolean,
    val isParent: Boolean = false,
    val isImage: Boolean = false,
    val mimeType: String? = null,
    val meta: String,
    var thumbnail: Bitmap? = null,
)
