package com.tkleisas.pinapp;

import android.app.Activity;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.util.Log;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.FileOutputStream;

/**
 * Minimal deterministic POS PIN app for the MechMaker HIL POC.
 *
 * A 3 x 4 keypad (1..9, CLR, 0, OK) on a light 720 x 1440 surface. The entered
 * PIN is shown masked, logged as PIN_ACCEPTED=<pin>, and written to files/pin.txt
 * so the host can verify the physical taps.
 */
public class MainActivity extends Activity {
    private static final String TAG = "PinApp";
    private static final int PIN_LENGTH = 4;

    private final StringBuilder pin = new StringBuilder();
    private TextView pinDisplay;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        setFullscreen();
        setContentView(buildUi());
        updateDisplay();
    }

    private void setFullscreen() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }

    private View buildUi() {
        final int pad = dp(24);
        final int gap = dp(10);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.rgb(240, 244, 250));
        root.setPadding(pad, dp(36), pad, dp(36));

        TextView title = new TextView(this);
        title.setText("ENTER PIN");
        title.setTextColor(Color.rgb(35, 38, 48));
        title.setTextSize(34);
        title.setTypeface(Typeface.DEFAULT_BOLD);
        title.setGravity(Gravity.CENTER);
        root.addView(title, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 0.8f));

        pinDisplay = new TextView(this);
        pinDisplay.setTextColor(Color.rgb(40, 70, 140));
        pinDisplay.setTextSize(40);
        pinDisplay.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        pinDisplay.setGravity(Gravity.CENTER);
        root.addView(pinDisplay, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 0.8f));

        LinearLayout keypad = new LinearLayout(this);
        keypad.setOrientation(LinearLayout.VERTICAL);
        root.addView(keypad, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 4.2f));

        String[][] rows = {
                {"1", "2", "3"},
                {"4", "5", "6"},
                {"7", "8", "9"},
                {"CLR", "0", "OK"}
        };
        for (String[] row : rows) {
            LinearLayout rowView = new LinearLayout(this);
            rowView.setOrientation(LinearLayout.HORIZONTAL);
            keypad.addView(rowView, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));

            for (String label : row) {
                Button button = new Button(this);
                button.setText(label);
                button.setTextSize(label.length() == 1 ? 40 : 24);
                button.setAllCaps(false);
                button.setTextColor(Color.rgb(25, 30, 45));
                button.setBackgroundColor(Color.rgb(218, 226, 238));
                button.setOnClickListener(v -> onKey(label));
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                        0, LinearLayout.LayoutParams.MATCH_PARENT, 1f);
                lp.setMargins(gap, gap, gap, gap);
                rowView.addView(button, lp);
            }
        }

        return root;
    }

    private void onKey(String label) {
        if (label.equals("CLR")) {
            pin.setLength(0);
        } else if (label.equals("OK")) {
            if (pin.length() == PIN_LENGTH) {
                accept();
                return;
            }
        } else if (pin.length() < PIN_LENGTH) {
            pin.append(label);
        }
        updateDisplay();
    }

    private void accept() {
        String value = pin.toString();
        Log.i(TAG, "PIN_ACCEPTED=" + value);
        try (FileOutputStream out = openFileOutput("pin.txt", MODE_PRIVATE)) {
            out.write(value.getBytes("UTF-8"));
        } catch (Exception e) {
            Log.e(TAG, "failed to write pin.txt", e);
        }
        pinDisplay.setText("APPROVED");
        pinDisplay.setTextColor(Color.rgb(30, 130, 55));
    }

    private void updateDisplay() {
        StringBuilder masked = new StringBuilder();
        for (int i = 0; i < pin.length(); i++) masked.append('*');
        pinDisplay.setText(masked.length() == 0 ? "----" : masked.toString());
        pinDisplay.setTextColor(Color.rgb(40, 70, 140));
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
