package app.plainspoken.ui

import android.app.Activity
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.os.Build
import android.text.InputType
import android.text.method.LinkMovementMethod
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.Button
import android.widget.CompoundButton
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

/**
 * Small helpers to build the app's screens in code with the Plainspoken look: cards on a soft background,
 * teal buttons, clear text. Plain Android widgets only (no extra libraries).
 */
class Ui(val context: Context) {
    val palette = Palette.of(context)

    fun column(padding: Int = 0): LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        val px = context.dp(padding)
        setPadding(px, px, px, px)
    }

    fun row(): LinearLayout = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
    }

    /** A screen: top bar with an optional back button, then a scrolling column. Handles system-bar insets. */
    fun screen(activity: Activity, title: String, showBack: Boolean): Pair<View, LinearLayout> {
        val root = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(palette.background)
        }

        val bar = row().apply {
            setPadding(context.dp(8), context.dp(8), context.dp(16), context.dp(8))
            minimumHeight = context.dp(56)
        }

        if (showBack) {
            bar.addView(iconButton(Icon.BACK, "Back") { activity.finish() }, LinearLayout.LayoutParams(context.dp(48), context.dp(48)))
        } else {
            bar.addView(View(context), LinearLayout.LayoutParams(context.dp(8), 1))
        }

        bar.addView(TextView(context).apply {
            text = title
            setTextColor(palette.text)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            setPadding(context.dp(8), 0, 0, 0)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))

        root.addView(bar)
        val content = column(16).apply { clipToPadding = false }
        val scroll = ScrollView(context).apply {
            isFillViewport = true
            addView(content)
        }

        root.addView(scroll, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        applySystemBarInsets(root)
        return root to content
    }

    /**
     * From Android 15 apps draw edge to edge, so pad the root to keep content clear of the status bar,
     * navigation bar and on-screen keyboard. Older versions do this themselves.
     */
    fun applySystemBarInsets(root: View) {
        if (Build.VERSION.SDK_INT < 35) return
        root.setOnApplyWindowInsetsListener { v, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime() or WindowInsets.Type.displayCutout())
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
    }

    fun card(parent: LinearLayout): LinearLayout {
        val card = column(16).apply {
            background = GradientDrawable().apply {
                setColor(palette.surface)
                cornerRadius = context.dpf(16f)
            }
        }

        parent.addView(card, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            bottomMargin = context.dp(12)
        })
        return card
    }

    fun heading(parent: LinearLayout, text: String): TextView = TextView(context).apply {
        this.text = text
        setTextColor(palette.text)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 17f)
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        setPadding(0, 0, 0, context.dp(6))
        parent.addView(this)
    }

    fun text(parent: LinearLayout, text: CharSequence, muted: Boolean = false, sizeSp: Float = 15f): TextView = TextView(context).apply {
        this.text = text
        setTextColor(if (muted) palette.muted else palette.text)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, sizeSp)
        setLineSpacing(0f, 1.15f)
        setPadding(0, context.dp(2), 0, context.dp(6))
        if (text is android.text.Spanned) movementMethod = LinkMovementMethod.getInstance()
        parent.addView(this)
    }

    fun button(parent: LinearLayout, label: String, primary: Boolean = true, onClick: () -> Unit): Button =
        styledButton(label, primary, onClick).also {
            parent.addView(it, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                topMargin = context.dp(6)
                bottomMargin = context.dp(4)
            })
        }

    fun styledButton(label: String, primary: Boolean, onClick: () -> Unit): Button = Button(context).apply {
        text = label
        isAllCaps = false
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        setTextColor(if (primary) palette.onBrand else palette.brand)
        stateListAnimator = null
        minHeight = context.dp(44)
        minimumHeight = context.dp(44)
        setPadding(context.dp(20), 0, context.dp(20), 0)
        val shape = GradientDrawable().apply {
            cornerRadius = context.dpf(22f)
            if (primary) setColor(palette.brand) else {
                setColor(palette.surface)
                setStroke(context.dp(1), palette.brand)
            }
        }

        background = RippleDrawable(ColorStateList.valueOf(palette.keyPressed), shape, null)
        setOnClickListener { onClick() }
    }

    fun iconButton(icon: Icon, description: String, onClick: () -> Unit): View = FrameLayout(context).apply {
        contentDescription = description
        isClickable = true
        isFocusable = true
        background = RippleDrawable(ColorStateList.valueOf(palette.keyPressed), null, GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(0xFFFFFFFF.toInt())
        })
        addView(IconView(context, icon, palette.text), FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setOnClickListener { onClick() }
    }

    fun toggle(parent: LinearLayout, label: String, note: String?, checked: Boolean, onChange: (Boolean) -> Unit): Switch {
        val row = row().apply { setPadding(0, context.dp(6), 0, context.dp(6)) }
        val labels = column()
        labels.addView(TextView(context).apply {
            text = label
            setTextColor(palette.text)
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
        })

        if (note != null) {
            labels.addView(TextView(context).apply {
                text = note
                setTextColor(palette.muted)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            })
        }

        row.addView(labels, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val switch = Switch(context).apply {
            isChecked = checked
            contentDescription = label
            setOnCheckedChangeListener { _: CompoundButton, value: Boolean -> onChange(value) }
        }

        row.addView(switch)
        row.setOnClickListener { switch.toggle() }
        parent.addView(row)
        return switch
    }

    fun <T> choice(parent: LinearLayout, options: List<Pair<T, String>>, selected: T, onChange: (T) -> Unit): RadioGroup {
        val group = RadioGroup(context).apply { orientation = RadioGroup.VERTICAL }
        options.forEachIndexed { index, (value, label) ->
            group.addView(RadioButton(context).apply {
                id = View.generateViewId()
                text = label
                setTextColor(palette.text)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                isChecked = value == selected
                buttonTintList = ColorStateList.valueOf(palette.brand)
                tag = index
                minHeight = context.dp(44)
            })
        }

        group.setOnCheckedChangeListener { g, checkedId ->
            val index = g.findViewById<View>(checkedId)?.tag as? Int ?: return@setOnCheckedChangeListener
            onChange(options[index].first)
        }

        parent.addView(group)
        return group
    }

    fun edit(parent: LinearLayout, hint: String, value: String = "", multiLine: Boolean = false, secret: Boolean = false): EditText = EditText(context).apply {
        this.hint = hint
        setText(value)
        setTextColor(palette.text)
        setHintTextColor(palette.muted)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
        backgroundTintList = ColorStateList.valueOf(palette.brand)
        inputType = when {
            secret -> InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            multiLine -> InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            else -> InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS or InputType.TYPE_TEXT_VARIATION_URI
        }

        if (multiLine) {
            minLines = 4
            maxLines = 12
            gravity = Gravity.TOP or Gravity.START
        } else {
            maxLines = 1
            isSingleLine = true
        }

        parent.addView(this, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
    }

    fun divider(parent: LinearLayout) {
        parent.addView(View(context).apply { setBackgroundColor(palette.divider) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, context.dp(1)).apply {
            topMargin = context.dp(8)
            bottomMargin = context.dp(8)
        })
    }
}
