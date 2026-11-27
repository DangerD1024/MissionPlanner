Neural speech engine in Mission Planner 3
=========================================

Mission Planner speaks its announcements (mode changes, waypoints, warnings) with a neural voice once one
has been downloaded in DATA > Actions > Speech... > Engine & voice; until then the system's own voice
speaks. The engine runs inside the app (no internet connection needed once a voice is downloaded):

sherpa-onnx 2.0.3        https://github.com/k2-fsa/sherpa-onnx
  Licence: Apache License 2.0 (LICENSE.sherpa-onnx.txt). Copyright Xiaomi Corporation and contributors.
  sherpa-onnx 2.x contains no eSpeak NG or other GPL code.

ONNX Runtime 1.28.2      https://onnxruntime.ai/
  Licence: MIT (LICENSE.onnxruntime.txt). Copyright Microsoft Corporation. Its third-party notices are in
  ThirdPartyNotices.txt of the Microsoft.ML.OnnxRuntime packages (https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime).

Only the libraries for the platform the app was made for are installed, next to the app. They come
unchanged from the NuGet packages org.k2fsa.sherpa.onnx.runtime.<platform> 2.0.3 (https://www.nuget.org/
packages/org.k2fsa.sherpa.onnx), published by the sherpa-onnx project; the managed API is the package
org.k2fsa.sherpa.onnx 2.0.3.

Voices are not part of the installer. Each is downloaded on request from sherpa-onnx's tts-models release
(https://github.com/k2-fsa/sherpa-onnx/releases/tag/tts-models) into the voices folder of Mission Planner's
data folder, and can be deleted there again:

  Kokoro v1.0 (fp32 and int8)   Apache-2.0  https://huggingface.co/hexgrad/Kokoro-82M
  Kitten TTS nano v0.8          Apache-2.0  https://github.com/KittenML/KittenTTS
  (Kitten also downloads sherpa-onnx's English pronunciation lexicon, lexicon-en-us.txt)


SHA-256 of the native libraries (as in the packages)
----------------------------------------------------

4b3607aebd1784b26b6f9b20e4bd974c7ab8287043e4d095cb7d2cb40b5e566e  linux-x64/libonnxruntime.so
641cea8ebc697d550212f781852d400a58295b51f4f55ee7854e84a82020fb4e  linux-x64/libsherpa-onnx-c-api.so
cfc800d4438f15e8d9017521326776ca7117d8783cb847ccccc2fc61ac4d540f  linux-arm64/libonnxruntime.so
3aa964fa128df4a6cf23a48d03802c7f216c3ecdb6c27fe92b5f6c183dd0c3cc  linux-arm64/libsherpa-onnx-c-api.so
7f66f939a881baf4f46a2216496798edf4a1429878b646d12674aa62f27d8a25  win-x64/onnxruntime.dll
7e68a48e07aec932f19193e9a6e11edf3b7de6ee80b5275c61ae2e1a59df99f6  win-x64/sherpa-onnx-c-api.dll
f97ee62a7b275bd660d5247d8921755f7bc31cccf8f6c4675d1e185fde8440f9  win-x86/onnxruntime.dll
2208225bedfa773a5f8172b927acf5a73f5c853fb06ec720d33b8ff100bc1388  win-x86/sherpa-onnx-c-api.dll
968d081836cad9af537105f59e34d4997bca2ce6d78ff39d3e47660620e8a642  win-arm64/onnxruntime.dll
c796501344d6586907cab3bbfa819eb2d2d1aebe3a55766fd6306826701982ab  win-arm64/sherpa-onnx-c-api.dll
2bf27180c09e6fd3f407c9ab25205ebf1ff95f2d53bfa35e35409b5849e0ac70  osx-x64/libonnxruntime.dylib
e00ef7d7a9a2cf267845a909f8511e4e44683448b55e0b33c2fff34a64382df6  osx-x64/libsherpa-onnx-c-api.dylib
b0613d0ae53199a83b05fa48e169211498e9d40d54beaa372068ebe5ec5b0929  osx-arm64/libonnxruntime.dylib
0a7562cab532282d3dda62d36fdb42b3fc472ac842b97a05e5f068d3fa8ee8fd  osx-arm64/libsherpa-onnx-c-api.dylib
