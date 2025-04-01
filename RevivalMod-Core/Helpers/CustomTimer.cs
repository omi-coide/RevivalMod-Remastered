using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace RevivalMod.Helpers
{
    /// <summary>
    /// Custom timer implementation with a fully custom UI
    /// </summary>
    public class CustomTimer
    {
        // Timer data
        private DateTime targetEndTime;
        private DateTime startTime;
        private bool isCountdown;
        private bool isRunning;
        private string timerName;

        // UI components
        private GameObject timerObject;
        private TextMeshProUGUI timerText;
        private TextMeshProUGUI titleText;

        public CustomTimer()
        {
        }

        /// <summary>
        /// Start a countdown timer with specified duration
        /// </summary>
        public void StartCountdown(float durationInSeconds, string name = "Countdown")
        {
            isCountdown = true;
            isRunning = true;
            timerName = name;

            // Set target time
            startTime = DateTime.UtcNow;
            targetEndTime = startTime.AddSeconds(durationInSeconds);

            // Create and show the timer UI
            CreateTimerUI();
        }

        /// <summary>
        /// Start a stopwatch to measure elapsed time
        /// </summary>
        public void StartStopwatch(string name = "Stopwatch")
        {
            isCountdown = false;
            isRunning = true;
            timerName = name;

            // Set start time
            startTime = DateTime.UtcNow;

            // Create and show the timer UI
            CreateTimerUI();
        }

        /// <summary>
        /// Update the timer (call this every frame)
        /// </summary>
        public void Update()
        {
            if (!isRunning || timerText == null)
                return;

            TimeSpan timeSpan = GetTimeSpan();

            // Check if countdown complete
            if (isCountdown && timeSpan.TotalSeconds <= 0)
            {
                timerText.text = "00:00";

                // Auto-stop if countdown finished
                StopTimer();
                return;
            }

            // Update the display
            timerText.text = GetFormattedTime();
        }

        /// <summary>
        /// Stop the timer
        /// </summary>
        public void StopTimer()
        {
            isRunning = false;

            if (timerObject != null)
            {
                GameObject.Destroy(timerObject);
                timerObject = null;
                timerText = null;
                titleText = null;
            }
        }

        /// <summary>
        /// Get current timer value as TimeSpan
        /// </summary>
        public TimeSpan GetTimeSpan()
        {
            if (isCountdown)
            {
                TimeSpan remaining = targetEndTime - DateTime.UtcNow;
                return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
            }
            else
            {
                return DateTime.UtcNow - startTime;
            }
        }

        /// <summary>
        /// Get the current timer value as formatted string (MM:SS)
        /// </summary>
        public string GetFormattedTime()
        {
            TimeSpan timeSpan = GetTimeSpan();

            if (isCountdown && timeSpan.TotalSeconds <= 0)
                return "00:00";

            return string.Format("{0:00}:{1:00}", (int)timeSpan.TotalMinutes, timeSpan.Seconds);
        }

        /// <summary>
        /// Create a custom timer UI and add it to the main canvas
        /// </summary>
        private void CreateTimerUI()
        {
            try
            {
                // Find the main canvas
                Canvas mainCanvas = FindMainCanvas();
                if (mainCanvas == null)
                {
                    Plugin.LogSource.LogError("Could not find main canvas");
                    return;
                }

                // Create timer object
                timerObject = new GameObject($"RevivalMod_{timerName}");
                timerObject.transform.SetParent(mainCanvas.transform, false);

                // Add RectTransform (required for UI elements)
                RectTransform rectTransform = timerObject.AddComponent<RectTransform>();

                // Set position and size
                if (isCountdown)
                {
                    // Bottom center for countdown
                    rectTransform.anchorMin = new Vector2(0.5f, 0);
                    rectTransform.anchorMax = new Vector2(0.5f, 0);
                    rectTransform.pivot = new Vector2(0.5f, 0);
                    rectTransform.anchoredPosition = new Vector2(0, 80);
                }
                else
                {
                    // Top center for stopwatch
                    rectTransform.anchorMin = new Vector2(0.5f, 1);
                    rectTransform.anchorMax = new Vector2(0.5f, 1);
                    rectTransform.pivot = new Vector2(0.5f, 1);
                    rectTransform.anchoredPosition = new Vector2(0, -50);
                }

                rectTransform.sizeDelta = new Vector2(200, 60);

                // Add a background image
                GameObject backgroundObj = new GameObject("Background");
                backgroundObj.transform.SetParent(timerObject.transform, false);
                RectTransform bgRectTransform = backgroundObj.AddComponent<RectTransform>();
                bgRectTransform.anchorMin = Vector2.zero;
                bgRectTransform.anchorMax = Vector2.one;
                bgRectTransform.offsetMin = Vector2.zero;
                bgRectTransform.offsetMax = Vector2.zero;

                Image bgImage = backgroundObj.AddComponent<Image>();
                bgImage.color = new Color(0, 0, 0, 0.5f);  // Semi-transparent black

                // Create title text
                GameObject titleObj = new GameObject("Title");
                titleObj.transform.SetParent(timerObject.transform, false);
                RectTransform titleRect = titleObj.AddComponent<RectTransform>();
                titleRect.anchorMin = new Vector2(0, 1);
                titleRect.anchorMax = new Vector2(1, 1);
                titleRect.pivot = new Vector2(0.5f, 1);
                titleRect.offsetMin = new Vector2(10, -25);
                titleRect.offsetMax = new Vector2(-10, 0);

                titleText = titleObj.AddComponent<TextMeshProUGUI>();
                titleText.text = timerName;
                titleText.fontSize = 16;
                titleText.alignment = TextAlignmentOptions.Center;
                titleText.color = Color.white;

                // Create timer text
                GameObject textObj = new GameObject("TimerText");
                textObj.transform.SetParent(timerObject.transform, false);
                RectTransform textRect = textObj.AddComponent<RectTransform>();
                textRect.anchorMin = new Vector2(0, 0);
                textRect.anchorMax = new Vector2(1, 1);
                textRect.pivot = new Vector2(0.5f, 0.5f);
                textRect.offsetMin = new Vector2(10, 10);
                textRect.offsetMax = new Vector2(-10, -25);

                timerText = textObj.AddComponent<TextMeshProUGUI>();
                timerText.text = GetFormattedTime();
                timerText.fontSize = 24;
                timerText.fontStyle = FontStyles.Bold;
                timerText.alignment = TextAlignmentOptions.Center;
                timerText.color = isCountdown ? Color.yellow : Color.green;

                // Make sure the object is active
                timerObject.SetActive(true);

                Plugin.LogSource.LogInfo($"Created custom timer UI for {timerName}");
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"Error creating custom timer UI: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Find the main canvas in the scene
        /// </summary>
        private Canvas FindMainCanvas()
        {
            // Try to find the main HUD canvas first
            Canvas[] canvases = GameObject.FindObjectsOfType<Canvas>();

            // Look for specific canvases in order of preference
            foreach (Canvas canvas in canvases)
            {
                // Check for common EFT canvas names
                if (canvas.name.Contains("HUD") || canvas.name.Contains("UI") ||
                    canvas.name.Contains("Menu") || canvas.name.Contains("GameUI"))
                {
                    return canvas;
                }
            }

            // Fall back to any canvas with screen space overlay mode
            foreach (Canvas canvas in canvases)
            {
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    return canvas;
                }
            }

            // Last resort - just use the first canvas
            if (canvases.Length > 0)
            {
                return canvases[0];
            }

            return null;
        }
    }
}