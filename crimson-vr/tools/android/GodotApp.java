package com.godot.game;

import org.godotengine.godot.Godot;
import org.godotengine.godot.GodotActivity;
import org.godotengine.godot.plugin.GodotPlugin;

import android.os.Bundle;
import android.util.Log;

import androidx.activity.EdgeToEdge;
import androidx.core.splashscreen.SplashScreen;

import java.util.HashSet;
import java.util.Set;

/** Godot Android activity plus the native Quest text-input bridge. */
public class GodotApp extends GodotActivity {
    static {
        if (BuildConfig.FLAVOR.equals("mono")) {
            try {
                Log.v("GODOT", "Loading System.Security.Cryptography.Native.Android library");
                System.loadLibrary("System.Security.Cryptography.Native.Android");
            } catch (UnsatisfiedLinkError e) {
                Log.e("GODOT", "Unable to load System.Security.Cryptography.Native.Android library");
            }
        }
    }

    private CrimsonTextInputPlugin textInputPlugin;

    private final Runnable updateWindowAppearance = () -> {
        Godot godot = getGodot();
        if (godot != null) {
            godot.enableImmersiveMode(godot.isInImmersiveMode(), true);
            godot.enableEdgeToEdge(godot.isInEdgeToEdgeMode(), true);
            godot.setSystemBarsAppearance();
        }
    };

    @Override
    public Set<GodotPlugin> getHostPlugins(Godot godot) {
        Set<GodotPlugin> plugins = new HashSet<>(super.getHostPlugins(godot));
        if (textInputPlugin == null) textInputPlugin = new CrimsonTextInputPlugin(godot);
        plugins.add(textInputPlugin);
        return plugins;
    }

    @Override
    public void onCreate(Bundle savedInstanceState) {
        SplashScreen splashScreen = SplashScreen.installSplashScreen(this);
        EdgeToEdge.enable(this);
        super.onCreate(savedInstanceState);
        Godot godot = getGodot();
        if (godot != null && godot.getDisableGodotSplash()) {
            splashScreen.setKeepOnScreenCondition(() -> godot.getRunStatus() != Godot.RunStatus.STARTED);
        }
    }

    @Override public void onResume() { super.onResume(); updateWindowAppearance.run(); }

    @Override
    public void onGodotMainLoopStarted() {
        super.onGodotMainLoopStarted();
        runOnUiThread(updateWindowAppearance);
    }

    @Override
    public void onGodotForceQuit(Godot instance) {
        if (!BuildConfig.FLAVOR.equals("instrumented")) super.onGodotForceQuit(instance);
    }

    @Override protected boolean isPiPEnabled() { return true; }
}
