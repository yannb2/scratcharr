# Scratcharr: the app plus the tools it drives, each at a pinned version.
# The weekly "Tool updates" workflow bumps these ARG lines in a pull request; nothing updates on its own.
ARG SEVENZIP_VERSION=26.06

# 7-Zip (7zz) from the official ip7z releases: unpacks .rar sets (RAR4 and RAR5), .7z and .zip.
FROM debian:13-slim AS sevenzip
ARG SEVENZIP_VERSION
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates xz-utils \
    && rm -rf /var/lib/apt/lists/*
RUN set -eux; \
    short="$(echo "${SEVENZIP_VERSION}" | tr -d .)"; \
    curl -fsSL "https://github.com/ip7z/7zip/releases/download/${SEVENZIP_VERSION}/7z${short}-linux-x64.tar.xz" -o /tmp/7z.tar.xz; \
    mkdir -p /tmp/7z /out; \
    tar -xJf /tmp/7z.tar.xz -C /tmp/7z; \
    install -m 0755 /tmp/7z/7zz /out/7zz; \
    /out/7zz | head -n 3

FROM mcr.microsoft.com/dotnet/sdk:10.0-resolute AS build
WORKDIR /src
COPY global.json ./
COPY src/Scratcharr/Scratcharr.csproj src/Scratcharr/
# arrkit is a git submodule: CI checks it out (submodules: true); locally run
# "git submodule update --init" before building.
COPY arrkit/src/Arrkit/Arrkit.csproj arrkit/src/Arrkit/
RUN dotnet restore src/Scratcharr/Scratcharr.csproj
COPY src/ src/
COPY arrkit/src/ arrkit/src/
RUN dotnet publish src/Scratcharr/Scratcharr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-resolute
ARG SEVENZIP_VERSION

RUN apt-get update && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

COPY --from=sevenzip /out/7zz /usr/local/bin/7zz

WORKDIR /app
COPY --from=build /app ./

# /config holds scratcharr.db (settings saved on the Settings page, first-seen times).
RUN mkdir -p /config && chown $APP_UID /config
VOLUME /config

# UTF-8 locale: in the plain C locale tools garble non-ASCII file names ("Me²").
# The pins show on the Tools page next to what each tool reports and the newest upstream version.
ENV ASPNETCORE_HTTP_PORTS=8188 \
    TZ=Etc/UTC \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    SCRATCHARR_PIN_7ZIP=${SEVENZIP_VERSION}
EXPOSE 8188

# Compose picks the user (user: 1000:1000), the same as qBittorrent and SABnzbd.
USER $APP_UID
ENTRYPOINT ["dotnet", "Scratcharr.dll"]
