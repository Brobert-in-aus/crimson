package com.godot.game;

import android.app.Activity;
import android.graphics.Color;
import android.text.Editable;
import android.text.InputFilter;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.EditText;
import android.widget.FrameLayout;

import org.godotengine.godot.Godot;
import org.godotengine.godot.plugin.GodotPlugin;
import org.godotengine.godot.plugin.UsedByGodot;

import java.util.concurrent.atomic.AtomicBoolean;

/** Provides Quest's IME with a real Android text editor to serve. */
public final class CrimsonTextInputPlugin extends GodotPlugin {
    private volatile String text = "";
    private final AtomicBoolean submitted = new AtomicBoolean(false);
    private EditText editor;
    private boolean changingText;

    public CrimsonTextInputPlugin(Godot godot) {
        super(godot);
    }

    @Override
    public String getPluginName() {
        return "CrimsonTextInput";
    }

    @UsedByGodot
    public void showKeyboard(String initialText, int maxLength, boolean roomCode) {
        text = initialText == null ? "" : initialText;
        submitted.set(false);
        runOnUiThread(() -> showOnUiThread(maxLength, roomCode));
    }

    @UsedByGodot
    public String getText() {
        return text;
    }

    @UsedByGodot
    public boolean takeSubmitted() {
        return submitted.getAndSet(false);
    }

    @UsedByGodot
    public void hideKeyboard() {
        runOnUiThread(this::hideOnUiThread);
    }

    private void showOnUiThread(int maxLength, boolean roomCode) {
        Activity activity = getActivity();
        if (activity == null) return;

        if (editor == null) {
            editor = new EditText(activity);
            editor.setSingleLine(true);
            editor.setImeOptions(EditorInfo.IME_ACTION_DONE | EditorInfo.IME_FLAG_NO_EXTRACT_UI);
            editor.setBackgroundColor(Color.TRANSPARENT);
            editor.setTextColor(Color.TRANSPARENT);
            editor.setCursorVisible(false);
            editor.setAlpha(0.01f);
            editor.setImportantForAutofill(android.view.View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
            editor.addTextChangedListener(new TextWatcher() {
                @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
                @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                    if (!changingText) text = s.toString();
                }
                @Override public void afterTextChanged(Editable s) {}
            });
            editor.setOnEditorActionListener((view, actionId, event) -> {
                if (actionId == EditorInfo.IME_ACTION_DONE ||
                        (event != null && event.getKeyCode() == android.view.KeyEvent.KEYCODE_ENTER)) {
                    text = editor.getText().toString();
                    submitted.set(true);
                    hideOnUiThread();
                    return true;
                }
                return false;
            });
            FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(2, 2);
            params.leftMargin = 1;
            params.topMargin = 1;
            activity.addContentView(editor, params);
        }

        changingText = true;
        editor.setFilters(new InputFilter[] { new InputFilter.LengthFilter(Math.max(1, maxLength)) });
        int flags = InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
        if (roomCode) flags |= InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS;
        else flags |= InputType.TYPE_TEXT_FLAG_CAP_WORDS;
        editor.setInputType(flags);
        editor.setText(text);
        editor.setSelection(editor.length());
        changingText = false;
        editor.setFocusableInTouchMode(true);
        editor.requestFocus();

        // Posting after focus gives InputMethodManager a served View/window token.
        editor.postDelayed(() -> {
            InputMethodManager imm = (InputMethodManager) activity.getSystemService(Activity.INPUT_METHOD_SERVICE);
            if (imm != null) imm.showSoftInput(editor, InputMethodManager.SHOW_IMPLICIT);
        }, 120);
    }

    private void hideOnUiThread() {
        Activity activity = getActivity();
        if (activity == null || editor == null) return;
        InputMethodManager imm = (InputMethodManager) activity.getSystemService(Activity.INPUT_METHOD_SERVICE);
        if (imm != null) imm.hideSoftInputFromWindow(editor.getWindowToken(), 0);
        editor.clearFocus();
    }
}
