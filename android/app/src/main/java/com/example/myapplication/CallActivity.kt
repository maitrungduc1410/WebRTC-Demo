package com.example.myapplication

import android.Manifest
import android.app.PictureInPictureParams
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.util.Rational
import android.view.WindowManager
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.annotation.RequiresApi
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.unit.IntSize
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.lifecycleScope
import com.example.myapplication.call.BaseCallViewModel
import com.example.myapplication.call.ConnectionPhase
import com.example.myapplication.call.CallViewModel
import com.example.myapplication.call.GroupCallViewModel
import com.example.myapplication.ui.call.CallScreen
import com.example.myapplication.ui.call.GroupCallScreen
import com.example.myapplication.ui.theme.AppTheme
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

class CallActivity : ComponentActivity() {

    companion object {
        private val RequiredPermissions = arrayOf(
            Manifest.permission.CAMERA,
            Manifest.permission.RECORD_AUDIO
        )

        // PictureInPictureParams rejects aspect ratios outside 1:2.39 .. 2.39:1.
        private const val MAX_PIP_RATIO = 2.39f
        private val DefaultPipRatio = Rational(9, 16)
    }

    private val group by lazy { intent.getBooleanExtra(BaseCallViewModel.EXTRA_GROUP, false) }

    private val viewModel: BaseCallViewModel by lazy {
        val provider = ViewModelProvider(this)
        if (group) provider[GroupCallViewModel::class.java] else provider[CallViewModel::class.java]
    }

    private var inPip by mutableStateOf(false)
    private var remoteFrameSize = IntSize.Zero

    private val pipSupported by lazy {
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)
    }

    /** Only a call with someone in it is worth a floating window. */
    private val canEnterPip: Boolean
        get() = pipSupported && viewModel.ui.value.phase == ConnectionPhase.Connected

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        } else {
            @Suppress("DEPRECATION")
            window.addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        if (pipSupported) setUpPip()

        setContent {
            AppTheme(darkTheme = true) {
                val permissionLauncher = rememberLauncherForActivityResult(
                    ActivityResultContracts.RequestMultiplePermissions()
                ) { results ->
                    if (results.values.all { it }) {
                        viewModel.startMedia()
                    } else {
                        Toast.makeText(this, "Camera and microphone access are required for calls", Toast.LENGTH_LONG).show()
                    }
                }
                val screenCaptureLauncher = rememberLauncherForActivityResult(
                    ActivityResultContracts.StartActivityForResult()
                ) { result ->
                    val data = result.data
                    if (result.resultCode == RESULT_OK && data != null) viewModel.startScreenShare(data)
                }
                val galleryLauncher = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri ->
                    uri?.let(viewModel::shareVideoFile)
                }
                val fileLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
                    uri?.let(viewModel::shareVideoFile)
                }

                LaunchedEffect(Unit) {
                    if (hasPermissions()) viewModel.startMedia() else permissionLauncher.launch(RequiredPermissions)
                }

                val onShareScreen = {
                    val manager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                    screenCaptureLauncher.launch(manager.createScreenCaptureIntent())
                }
                val onShareFromGallery = { galleryLauncher.launch("video/*") }
                val onShareFromFiles = { fileLauncher.launch(arrayOf("video/*", "video/mp4", "video/webm")) }
                when (val vm = viewModel) {
                    is GroupCallViewModel -> GroupCallScreen(
                        vm = vm,
                        inPip = inPip,
                        onHangUp = { finish() },
                        onShareScreen = onShareScreen,
                        onShareFromGallery = onShareFromGallery,
                        onShareFromFiles = onShareFromFiles,
                        onEnterPip = if (pipSupported) ::enterPip else null
                    )
                    is CallViewModel -> CallScreen(
                        vm = vm,
                        inPip = inPip,
                        onHangUp = { finish() },
                        onShareScreen = onShareScreen,
                        onShareFromGallery = onShareFromGallery,
                        onShareFromFiles = onShareFromFiles,
                        onEnterPip = if (pipSupported) ::enterPip else null,
                        onRemoteFrameSize = ::onRemoteFrameSize
                    )
                }
            }
        }

        // There is no reconnect: a closed signaling socket (or a full room) ends either kind of call.
        lifecycleScope.launch {
            val message = viewModel.callEnded.filterNotNull().first()
            Toast.makeText(this@CallActivity, message, Toast.LENGTH_LONG).show()
            finish()
        }
    }

    private fun hasPermissions() = RequiredPermissions.all {
        ContextCompat.checkSelfPermission(this, it) == PackageManager.PERMISSION_GRANTED
    }

    override fun onResume() {
        super.onResume()
        viewModel.onForegroundChanged(true)
    }

    override fun onPause() {
        super.onPause()
        viewModel.onForegroundChanged(false)
    }

    // ========== Picture-in-picture ==========

    private fun setUpPip() {
        addOnPictureInPictureModeChangedListener { info ->
            inPip = info.isInPictureInPictureMode
            // Closing the PiP window stops the activity without bringing it back. Nothing keeps
            // the camera and microphone alive in the background, so that ends the call.
            if (!info.isInPictureInPictureMode && lifecycle.currentState == Lifecycle.State.CREATED) finish()
        }
        lifecycleScope.launch {
            viewModel.ui.map { it.phase == ConnectionPhase.Connected }
                .distinctUntilChanged()
                .collect { updatePipParams() }
        }
    }

    /** Android 12+ enters PiP by itself on the home gesture ([PictureInPictureParams.Builder.setAutoEnterEnabled]). */
    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S && canEnterPip) enterPip()
    }

    private fun enterPip() {
        if (!canEnterPip || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        try {
            enterPictureInPictureMode(pipParams())
        } catch (e: IllegalStateException) {
            // Thrown when the user turned picture-in-picture off for this app.
            Toast.makeText(this, "Picture-in-picture is turned off for this app", Toast.LENGTH_SHORT).show()
        }
    }

    /** Called from the render thread whenever the remote frame size changes. */
    private fun onRemoteFrameSize(size: IntSize) {
        runOnUiThread {
            if (size == remoteFrameSize) return@runOnUiThread
            remoteFrameSize = size
            updatePipParams()
        }
    }

    private fun updatePipParams() {
        if (!pipSupported || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        setPictureInPictureParams(pipParams())
    }

    @RequiresApi(Build.VERSION_CODES.O)
    private fun pipParams(): PictureInPictureParams {
        val builder = PictureInPictureParams.Builder().setAspectRatio(pipRatio())
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setAutoEnterEnabled(canEnterPip)
            // Video content: a crossfade looks better than stretching the old frame.
            builder.setSeamlessResizeEnabled(false)
        }
        return builder.build()
    }

    private fun pipRatio(): Rational {
        val (width, height) = remoteFrameSize
        if (width <= 0 || height <= 0) return DefaultPipRatio
        val ratio = width.toFloat() / height
        return when {
            ratio > MAX_PIP_RATIO -> Rational(239, 100)
            ratio < 1 / MAX_PIP_RATIO -> Rational(100, 239)
            else -> Rational(width, height)
        }
    }
}
