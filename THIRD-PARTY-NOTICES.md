# Third-party notices

Plainspoken is licensed under the GNU General Public License v3.0 (see `LICENSE`).
The releases bundle the following third-party components, each under its own licence
(all compatible with the GPL-3.0).

# Windows app

## NAudio (NAudio.Core, NAudio.Wasapi 2.2.1)

Microphone capture. https://github.com/naudio/NAudio

```
MIT License

Copyright (c) 2023 Mark Heath

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## .NET runtime and Windows Forms

The self-contained `Plainspoken.exe` includes the .NET 10 runtime and Windows Forms, © .NET Foundation
and contributors, MIT License. Their own third-party notices are published at
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and
https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT.

# Android app

The Android APK bundles these libraries, all under the Apache License 2.0
(https://www.apache.org/licenses/LICENSE-2.0):

- **OkHttp** and **Okio** — HTTP and WebSocket client. © Square, Inc. https://github.com/square/okhttp
- **Kotlin standard library**, **kotlinx.coroutines** and **kotlinx.serialization** — © JetBrains s.r.o. and
  Kotlin contributors. https://github.com/JetBrains/kotlin, https://github.com/Kotlin/kotlinx.coroutines,
  https://github.com/Kotlin/kotlinx.serialization

No Google Play Services or other Google libraries are included.

# Both apps

## Google Gemini API

Plainspoken calls Google's Gemini API with *your own* API key. Your use of the API is governed by
Google's terms: https://ai.google.dev/gemini-api/terms. No Google code is bundled.
