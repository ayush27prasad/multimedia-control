package com.superbryn.phonebridge.media

import android.content.Context
import android.util.Log
import android.util.Size
import androidx.camera.core.Camera
import androidx.camera.core.CameraSelector
import androidx.camera.core.FocusMeteringAction
import androidx.camera.core.Preview
import androidx.camera.core.SurfaceOrientedMeteringPointFactory
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import com.superbryn.phonebridge.state.CameraState
import com.superbryn.phonebridge.transport.FrameSink
import java.util.concurrent.Executor

private const val TAG = "CameraController"

/**
 * CameraX capture wired straight into [VideoEncoder]'s input Surface (FR1, FR3).
 *
 * Note on the CameraX use case: this uses [Preview], not VideoCapture. VideoCapture's
 * Recorder muxes to MP4, and we need a raw elementary stream. Preview lets us supply an
 * arbitrary consumer Surface - the encoder's - so frames go sensor -> encoder with no
 * intermediate container and no copy through the app heap.
 */
class CameraController(
    private val context: Context,
    private val lifecycleOwner: LifecycleOwner,
    private val onError: (String) -> Unit
) {
    private val mainExecutor: Executor = ContextCompat.getMainExecutor(context)

    private var provider: ProcessCameraProvider? = null
    private var encoder: VideoEncoder? = null
    private var camera: Camera? = null

    @Volatile var isStreaming: Boolean = false
        private set

    /** Facings the hardware actually reports, so the UI never offers a camera that is not there. */
    fun availableFacings(): List<String> {
        val p = provider ?: return emptyList()
        return buildList {
            if (p.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA)) add(CameraState.FACING_BACK)
            if (p.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA)) add(CameraState.FACING_FRONT)
        }
    }

    /** Must complete before any other call. Idempotent. */
    fun initialize(onReady: () -> Unit) {
        if (provider != null) { onReady(); return }
        val future = ProcessCameraProvider.getInstance(context)
        future.addListener({
            try {
                provider = future.get()
                onReady()
            } catch (e: Exception) {
                onError("camera provider unavailable: ${e.message}")
            }
        }, mainExecutor)
    }

    /**
     * Binds the camera and starts encoding. Must be called on the main thread - CameraX
     * requires it for bindToLifecycle.
     */
    fun start(config: VideoConfig, facing: String, sink: FrameSink) {
        val p = provider ?: run { onError("camera not initialised"); return }
        stop()

        try {
            val enc = VideoEncoder(config, sink, onError)
            val surface = enc.start()
            encoder = enc

            val selector = when (facing) {
                CameraState.FACING_FRONT -> CameraSelector.DEFAULT_FRONT_CAMERA
                else -> CameraSelector.DEFAULT_BACK_CAMERA
            }

            val preview = Preview.Builder()
                .setResolutionSelector(
                    ResolutionSelector.Builder()
                        .setResolutionStrategy(
                            // FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER: prefer over-shooting
                            // the requested size. Downscaling on the PC is cheap; upscaling
                            // cannot recover detail the sensor never captured (FR3 puts peak
                            // quality ahead of bandwidth).
                            ResolutionStrategy(
                                Size(config.width, config.height),
                                ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER
                            )
                        )
                        .build()
                )
                .build()

            preview.setSurfaceProvider { request ->
                request.provideSurface(surface, mainExecutor) { result ->
                    // The encoder owns this Surface and releases it in VideoEncoder.stop().
                    // Releasing it here too would double-free on a rebind.
                    Log.i(TAG, "surface released by CameraX: ${result.resultCode}")
                }
            }

            p.unbindAll()
            camera = p.bindToLifecycle(lifecycleOwner, selector, preview)
            lockFocus()

            isStreaming = true
            Log.i(TAG, "camera started facing=$facing")
        } catch (e: Exception) {
            Log.e(TAG, "camera start failed", e)
            stop()
            onError("camera start failed: ${e.message}")
        }
    }

    /** Must be called on the main thread. */
    fun stop() {
        isStreaming = false
        runCatching { provider?.unbindAll() }
        encoder?.stop()
        encoder = null
        camera = null
    }

    /**
     * Converges autofocus once on the centre of the frame and holds it there.
     *
     * A desk setup is a fixed-distance scene, so continuous AF has nothing to track and
     * every hunt is a visible artefact - the single most noticeable quality difference
     * against a fixed-focus webcam. disableAutoCancel() is what makes this a one-shot
     * converge-and-hold rather than a temporary override that reverts to continuous.
     *
     * Exposed so the UI can re-trigger it if the scene genuinely changes.
     */
    fun lockFocus() {
        val cam = camera ?: return
        try {
            // A surface-oriented factory needs no preview view; 1x1 normalised space
            // means (0.5, 0.5) is the centre regardless of the actual resolution.
            val point = SurfaceOrientedMeteringPointFactory(1f, 1f).createPoint(0.5f, 0.5f)
            val action = FocusMeteringAction
                .Builder(point, FocusMeteringAction.FLAG_AF or FocusMeteringAction.FLAG_AE)
                .disableAutoCancel()
                .build()
            cam.cameraControl.startFocusAndMetering(action)
            Log.i(TAG, "autofocus converged and locked on centre")
        } catch (e: Exception) {
            // Some devices refuse metering on some cameras; continuous AF is a worse
            // default but not a failure worth aborting the stream over.
            Log.w(TAG, "focus lock unavailable: ${e.message}")
        }
    }

    /** Forces an IDR so a freshly attached PC decodes immediately. */
    fun requestKeyframe() = encoder?.requestKeyframe()
}
