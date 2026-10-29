# Image de build Android (.NET 10 + workload MAUI + SDK Android). Utilisée par la CI (job android)
# et pour builder l'APK en local sans installer Android Studio :
#   docker build -f build/android-sdk.Dockerfile -t localify-android-sdk build
#   docker run --rm -v "$PWD:/src" -w /src localify-android-sdk \
#     dotnet publish src/Localify.Mobile -f net10.0-android -c Release -o out/android
FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG ANDROID_API=36
ARG ANDROID_BUILD_TOOLS=36.0.0
ARG CMDLINE_TOOLS=13114758

ENV ANDROID_HOME=/opt/android-sdk \
    AndroidSdkDirectory=/opt/android-sdk \
    JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends openjdk-21-jdk-headless unzip \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p $ANDROID_HOME/cmdline-tools \
    && curl -fsSL -o /tmp/tools.zip https://dl.google.com/android/repository/commandlinetools-linux-${CMDLINE_TOOLS}_latest.zip \
    && unzip -q /tmp/tools.zip -d $ANDROID_HOME/cmdline-tools \
    && mv $ANDROID_HOME/cmdline-tools/cmdline-tools $ANDROID_HOME/cmdline-tools/latest \
    && rm /tmp/tools.zip \
    && yes | $ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager --licenses > /dev/null \
    && $ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager "platform-tools" "platforms;android-${ANDROID_API}" "build-tools;${ANDROID_BUILD_TOOLS}" > /dev/null

RUN dotnet workload install maui-android --skip-sign-check
