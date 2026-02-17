package com.nanored.vpn.ui

import android.animation.AnimatorSet
import android.animation.ArgbEvaluator
import android.animation.ValueAnimator
import android.graphics.drawable.GradientDrawable
import android.view.View
import android.view.ViewGroup
import android.view.animation.AccelerateDecelerateInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.content.ContextCompat
import com.nanored.vpn.R

class VpnButtonAnimator(
    private val container: FrameLayout,
    private val icon: ImageView,
    private val contentLayout: LinearLayout,
    private val tvTime: TextView,
    private val tvSpeed: TextView,
    private val tvPing: TextView
) {
    enum class State { IDLE, CONNECTING, CONNECTED, ERROR, DISCONNECTING }

    var currentState = State.IDLE
        private set

    private var currentAnimator: AnimatorSet? = null
    private var pulseAnimator: ValueAnimator? = null
    private var shimmerAnimator: ValueAnimator? = null

    private val background = GradientDrawable()
    private val context = container.context

    // Sizes in pixels
    private val circleSize = dpToPx(108)
    private val rectWidth = dpToPx(280)
    private val rectHeight = dpToPx(80)
    private val circleRadius = circleSize / 2f
    private val rectRadius = dpToPx(24).toFloat()

    // Colors
    private val colorIdle get() = ContextCompat.getColor(context, R.color.vpn_btn_idle)
    private val colorIdleShimmer get() = ContextCompat.getColor(context, R.color.vpn_btn_idle_shimmer)
    private val colorConnecting get() = ContextCompat.getColor(context, R.color.vpn_btn_connecting)
    private val colorConnected get() = ContextCompat.getColor(context, R.color.vpn_btn_connected)
    private val colorConnectedShimmer get() = ContextCompat.getColor(context, R.color.vpn_btn_connected_shimmer)
    private val colorError get() = ContextCompat.getColor(context, R.color.vpn_btn_error)
    private val colorErrorShimmer get() = ContextCompat.getColor(context, R.color.vpn_btn_error_shimmer)

    private val argbEvaluator = ArgbEvaluator()

    init {
        background.shape = GradientDrawable.RECTANGLE
        background.cornerRadius = circleRadius
        background.setColor(colorIdle)
        container.background = background
        container.clipToOutline = true

        setContainerSize(circleSize, circleSize)
        icon.alpha = 1f
        contentLayout.alpha = 0f
    }

    fun initialize() {
        startIdlePulse()
    }

    fun transitionTo(newState: State) {
        if (newState == currentState) return

        currentAnimator?.cancel()
        currentAnimator = null

        val prevState = currentState
        currentState = newState

        when (newState) {
            State.IDLE -> {
                stopShimmer()
                if (prevState == State.ERROR) {
                    // Already handled in error flow
                } else {
                    snapToIdle()
                }
                startIdlePulse()
            }
            State.CONNECTING -> {
                stopPulse()
                stopShimmer()
                animateConnecting()
            }
            State.CONNECTED -> {
                stopPulse()
                startConnectedShimmer()
            }
            State.ERROR -> {
                stopPulse()
                stopShimmer()
                animateError()
            }
            State.DISCONNECTING -> {
                stopShimmer()
                animateDisconnecting()
            }
        }
    }

    /**
     * Flash the expanded button back to connecting color without collapsing.
     * Used when switching countries while VPN is connected.
     */
    fun flashToConnecting() {
        currentAnimator?.cancel()
        currentAnimator = null
        stopShimmer()
        stopPulse()

        currentState = State.CONNECTING

        // Button stays expanded, just animate color change
        val colorFlash = ValueAnimator.ofObject(argbEvaluator, colorConnected, colorConnecting).apply {
            duration = 300
            addUpdateListener { anim ->
                background.setColor(anim.animatedValue as Int)
            }
        }
        colorFlash.start()
    }

    fun updateSessionInfo(time: String, speed: String, ping: String) {
        tvTime.text = time
        tvSpeed.text = speed
        tvPing.text = ping
    }

    // ===== IDLE: Pulsing color shimmer =====

    private fun startIdlePulse() {
        stopPulse()
        pulseAnimator = ValueAnimator.ofFloat(0f, 1f, 0f).apply {
            duration = 2500
            repeatCount = ValueAnimator.INFINITE
            interpolator = AccelerateDecelerateInterpolator()
            addUpdateListener { anim ->
                val fraction = anim.animatedValue as Float
                val color = argbEvaluator.evaluate(fraction, colorIdle, colorIdleShimmer) as Int
                background.setColor(color)
            }
            start()
        }
    }

    private fun stopPulse() {
        pulseAnimator?.cancel()
        pulseAnimator = null
    }

    // ===== CONNECTING: Circle -> Rectangle morph (fast expand) =====

    private fun animateConnecting() {
        icon.setImageResource(R.drawable.ic_play_24dp)

        val currentWidth = container.layoutParams.width.let { if (it > 0) it else circleSize }
        val currentHeight = container.layoutParams.height.let { if (it > 0) it else circleSize }
        val currentCorner = background.cornerRadius

        val set = AnimatorSet()

        // Width animation: current -> rect (fast: 600ms)
        val widthAnim = ValueAnimator.ofInt(currentWidth, rectWidth).apply {
            duration = 600
            addUpdateListener { anim ->
                val w = anim.animatedValue as Int
                val params = container.layoutParams
                params.width = w
                container.layoutParams = params
            }
        }

        // Height animation: current -> rect (fast: 600ms)
        val heightAnim = ValueAnimator.ofInt(currentHeight, rectHeight).apply {
            duration = 600
            addUpdateListener { anim ->
                val h = anim.animatedValue as Int
                val params = container.layoutParams
                params.height = h
                container.layoutParams = params
            }
        }

        // Corner radius animation: circle -> rounded rect
        val cornerAnim = ValueAnimator.ofFloat(currentCorner, rectRadius).apply {
            duration = 600
            addUpdateListener { anim ->
                background.cornerRadius = anim.animatedValue as Float
            }
        }

        // Color: stay connecting color (greenish tint to show activity)
        val colorAnim = ValueAnimator.ofObject(argbEvaluator, colorIdle, colorConnecting).apply {
            duration = 600
            addUpdateListener { anim ->
                background.setColor(anim.animatedValue as Int)
            }
        }

        // Icon fade out during morph
        val iconFade = ValueAnimator.ofFloat(icon.alpha, 0f).apply {
            duration = 300
            startDelay = 200
            addUpdateListener { anim ->
                icon.alpha = anim.animatedValue as Float
            }
        }

        // Content fade in after morph
        val contentFade = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 300
            startDelay = 500
            addUpdateListener { anim ->
                contentLayout.alpha = anim.animatedValue as Float
            }
        }

        set.playTogether(widthAnim, heightAnim, cornerAnim, colorAnim, iconFade, contentFade)
        set.interpolator = AccelerateDecelerateInterpolator()
        set.start()

        currentAnimator = set
    }

    // ===== CONNECTED: Green shimmer =====

    private fun startConnectedShimmer() {
        stopShimmer()

        // Ensure button is fully expanded (snap if animation was interrupted)
        setContainerSize(rectWidth, rectHeight)
        background.cornerRadius = rectRadius
        icon.alpha = 0f
        contentLayout.alpha = 1f

        // Smooth transition from current color to connected green, then shimmer
        val currentColor = colorConnecting
        val transitionToGreen = ValueAnimator.ofObject(argbEvaluator, currentColor, colorConnected).apply {
            duration = 400
            addUpdateListener { anim ->
                background.setColor(anim.animatedValue as Int)
            }
        }
        transitionToGreen.start()

        // Start shimmer after transition
        shimmerAnimator = ValueAnimator.ofFloat(0f, 1f, 0f).apply {
            startDelay = 400
            duration = 3000
            repeatCount = ValueAnimator.INFINITE
            interpolator = AccelerateDecelerateInterpolator()
            addUpdateListener { anim ->
                val fraction = anim.animatedValue as Float
                val color = argbEvaluator.evaluate(fraction, colorConnected, colorConnectedShimmer) as Int
                background.setColor(color)
            }
            start()
        }
    }

    private fun stopShimmer() {
        shimmerAnimator?.cancel()
        shimmerAnimator = null
    }

    // ===== ERROR: Flash red in expanded state -> collapse back to idle =====

    private fun animateError() {
        // Button is already expanded (rect) from CONNECTING state
        // Show error in expanded state, then collapse

        val currentWidth = container.layoutParams.width.let { if (it > 0) it else rectWidth }
        val currentHeight = container.layoutParams.height.let { if (it > 0) it else rectHeight }
        val currentCorner = background.cornerRadius

        val set = AnimatorSet()

        // Phase 1: Flash to red in expanded state (300ms)
        val flashIn = ValueAnimator.ofObject(argbEvaluator,
            colorConnecting, colorError
        ).apply {
            duration = 300
            addUpdateListener { anim ->
                background.setColor(anim.animatedValue as Int)
            }
        }

        // Phase 2: Hold red expanded (1500ms)
        val hold = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 1500
        }

        // Phase 3: Content fade out (300ms)
        val contentFadeOut = ValueAnimator.ofFloat(contentLayout.alpha, 0f).apply {
            duration = 300
            addUpdateListener { anim ->
                contentLayout.alpha = anim.animatedValue as Float
            }
        }

        // Phase 4: Collapse back to circle + fade color to idle (800ms)
        val collapseWidth = ValueAnimator.ofInt(currentWidth, circleSize).apply {
            duration = 800
            addUpdateListener { anim ->
                val w = anim.animatedValue as Int
                val params = container.layoutParams
                params.width = w
                container.layoutParams = params
            }
        }
        val collapseHeight = ValueAnimator.ofInt(currentHeight, circleSize).apply {
            duration = 800
            addUpdateListener { anim ->
                val h = anim.animatedValue as Int
                val params = container.layoutParams
                params.height = h
                container.layoutParams = params
            }
        }
        val collapseCorner = ValueAnimator.ofFloat(currentCorner, circleRadius).apply {
            duration = 800
            addUpdateListener { anim ->
                background.cornerRadius = anim.animatedValue as Float
            }
        }
        val colorFadeBack = ValueAnimator.ofObject(argbEvaluator, colorError, colorIdle).apply {
            duration = 800
            addUpdateListener { anim ->
                background.setColor(anim.animatedValue as Int)
            }
        }
        val iconFadeIn = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 400
            addUpdateListener { anim ->
                icon.alpha = anim.animatedValue as Float
            }
        }

        // Build sequence: flash -> hold -> fadeOutContent -> (collapse + colorFade + iconFadeIn)
        val collapseSet = AnimatorSet()
        collapseSet.playTogether(collapseWidth, collapseHeight, collapseCorner, colorFadeBack, iconFadeIn)
        collapseSet.interpolator = AccelerateDecelerateInterpolator()

        set.playSequentially(flashIn, hold, contentFadeOut, collapseSet)

        // After full animation, restore to IDLE
        colorFadeBack.addUpdateListener { anim ->
            if (anim.animatedFraction >= 1f) {
                icon.setImageResource(R.drawable.ic_play_24dp)
                currentState = State.IDLE
                startIdlePulse()
            }
        }

        icon.setImageResource(R.drawable.ic_stop_24dp)

        set.start()
        currentAnimator = set
    }

    // ===== DISCONNECTING: Rectangle -> Circle morph =====

    private fun animateDisconnecting() {
        val set = AnimatorSet()

        val currentWidth = container.layoutParams.width
        val currentHeight = container.layoutParams.height
        val currentRadius = background.cornerRadius

        // Content fade out first (0.5s)
        val contentFadeOut = ValueAnimator.ofFloat(contentLayout.alpha, 0f).apply {
            duration = 500
            addUpdateListener { anim ->
                contentLayout.alpha = anim.animatedValue as Float
            }
        }

        // Icon fade in (0.5s, starts with morph)
        val iconFadeIn = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 500
            startDelay = 500
            addUpdateListener { anim ->
                icon.alpha = anim.animatedValue as Float
            }
        }

        // Width animation: rect -> circle (starts after content fades)
        val widthAnim = ValueAnimator.ofInt(currentWidth, circleSize).apply {
            duration = 2500
            startDelay = 500
            addUpdateListener { anim ->
                val w = anim.animatedValue as Int
                val params = container.layoutParams
                params.width = w
                container.layoutParams = params
            }
        }

        // Height animation: rect -> circle
        val heightAnim = ValueAnimator.ofInt(currentHeight, circleSize).apply {
            duration = 2500
            startDelay = 500
            addUpdateListener { anim ->
                val h = anim.animatedValue as Int
                val params = container.layoutParams
                params.height = h
                container.layoutParams = params
            }
        }

        // Corner radius animation
        val cornerAnim = ValueAnimator.ofFloat(currentRadius, circleRadius).apply {
            duration = 2500
            startDelay = 500
            addUpdateListener { anim ->
                background.cornerRadius = anim.animatedValue as Float
            }
        }

        // Color animation: connected -> idle
        val colorAnim = ValueAnimator.ofObject(argbEvaluator, colorConnected, colorIdle).apply {
            duration = 2500
            startDelay = 500
            addUpdateListener { anim ->
                background.setColor(anim.animatedValue as Int)
            }
        }

        set.playTogether(contentFadeOut, iconFadeIn, widthAnim, heightAnim, cornerAnim, colorAnim)
        set.interpolator = AccelerateDecelerateInterpolator()
        set.start()

        currentAnimator = set
    }

    // ===== Helpers =====

    private fun snapToIdle() {
        setContainerSize(circleSize, circleSize)
        background.cornerRadius = circleRadius
        background.setColor(colorIdle)
        icon.setImageResource(R.drawable.ic_play_24dp)
        icon.alpha = 1f
        contentLayout.alpha = 0f
    }

    private fun setContainerSize(width: Int, height: Int) {
        val params = container.layoutParams
        params.width = width
        params.height = height
        container.layoutParams = params
    }

    private fun dpToPx(dp: Int): Int {
        return (dp * context.resources.displayMetrics.density).toInt()
    }

    fun destroy() {
        currentAnimator?.cancel()
        stopPulse()
        stopShimmer()
    }
}
