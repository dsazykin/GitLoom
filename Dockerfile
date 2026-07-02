# =============================================================================
# GitLoom — DEVELOPMENT / BUILD / CI container
# =============================================================================
# This image reproduces a *consistent build & test toolchain* across every
# contributor's machine (Windows, macOS, Linux). It pins the .NET 10 SDK plus
# the native libraries LibGit2Sharp and SkiaSharp need, and can run the full
# headless test suite (Avalonia.Headless) with no display server.
#
# IMPORTANT: This container is for BUILD, TEST, and EF migrations — NOT for
# running the GitLoom desktop GUI for end users. Shipping a desktop app inside
# a container means X11/Wayland forwarding, which is fragile and does NOT
# "run the same everywhere." End-user distribution stays native per-OS
# (Velopack: .exe / .dmg / .AppImage), as already planned in the roadmap.
# See "How to use" at the bottom of this file.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dev

# --- Native runtime deps -----------------------------------------------------
# git            : CLI fallback engine used by GitService (ExecuteGitCli et al.)
# libgit2 deps   : libssl / libssh2 for LibGit2Sharp network transport
# skia/font deps : libfontconfig1, libice6, libsm6, libx11-6 so SkiaSharp
#                  (LiveCharts, AvaloniaEdit, headless render tests) loads
# libicu         : globalization (already in SDK image, kept explicit)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        ca-certificates \
        libssl3 \
        libssh2-1 \
        libfontconfig1 \
        libice6 \
        libsm6 \
        libx11-6 \
        libicu-dev \
        xvfb \
    && rm -rf /var/lib/apt/lists/*

# Restore the local dotnet tools (dotnet-ef) declared in dotnet-tools.json.
ENV PATH="${PATH}:/root/.dotnet/tools"
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

WORKDIR /src

# --- Layer-cache the restore -------------------------------------------------
# Copy only the project graph first so `dotnet restore` is cached until a
# .csproj / solution actually changes.
COPY global.json GitLoom.slnx dotnet-tools.json ./
COPY GitLoom.App/GitLoom.App.csproj             GitLoom.App/
COPY GitLoom.Core/GitLoom.Core.csproj           GitLoom.Core/
COPY GitLoom.Tests/GitLoom.Tests.csproj         GitLoom.Tests/
COPY GitLoom.StyleTests/GitLoom.StyleTests.csproj   GitLoom.StyleTests/
COPY GitLoom.StyleConsole/GitLoom.StyleConsole.csproj GitLoom.StyleConsole/
RUN dotnet tool restore && dotnet restore GitLoom.slnx

# --- Copy the rest & build ---------------------------------------------------
COPY . .
RUN dotnet build GitLoom.slnx -c Release --no-restore

# Default: run the test suites headlessly.
CMD ["dotnet", "test", "GitLoom.slnx", "-c", "Release", "--no-build"]
