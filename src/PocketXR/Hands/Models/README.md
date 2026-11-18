# Hand models

`hand_detector.onnx` and `hand_landmarks_detector.onnx` are Google's MediaPipe hand landmarker models (the float16
`hand_landmarker.task`, version 1, the same file the Android app uses), converted from TensorFlow Lite to ONNX with
tf2onnx 1.16 (opset 17). Nothing else was changed. On the same random input, ONNX Runtime's outputs match TensorFlow
Lite's to within 0.00013 (checked 18 November 2026).

Source: https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task

Copyright Google LLC. Licensed under the Apache License, Version 2.0: https://www.apache.org/licenses/LICENSE-2.0
