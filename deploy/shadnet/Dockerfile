FROM ubuntu:24.04 AS build

ARG SHADNET_COMMIT=5a849a192fd4450593c57b10697316ac324c856e
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    git ca-certificates cmake ninja-build build-essential \
    qt6-base-dev qt6-httpserver-dev libqt6sql6-sqlite \
    && rm -rf /var/lib/apt/lists/*
RUN apt-get update && apt-get install -y --no-install-recommends \
    qt6-websockets-dev && rm -rf /var/lib/apt/lists/*
RUN git clone --recursive https://github.com/Wozzardman/shadnet-p2p.git /src \
    && cd /src \
    && git checkout --detach "$SHADNET_COMMIT" \
    && git submodule update --init --recursive \
    && test "$(git rev-parse HEAD)" = "$SHADNET_COMMIT"
COPY patches/qt64-server-api.patch /tmp/qt64-server-api.patch
RUN cd /src && git apply --unidiff-zero /tmp/qt64-server-api.patch
RUN --mount=type=cache,target=/src/build cmake -S /src -B /src/build -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF \
    && cmake --build /src/build --parallel 4 -- -k 0 \
    && mkdir -p /src/artifacts \
    && cp /src/build/shadnet /src/build/worlds.cfg \
       /src/build/scoreboards.cfg /src/artifacts/

FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl libqt6core6t64 libqt6network6t64 \
    libqt6sql6t64 libqt6sql6-sqlite libqt6httpserver6 \
    libqt6concurrent6 \
    && rm -rf /var/lib/apt/lists/*
RUN groupadd --gid 10001 shadnet && useradd --uid 10001 --gid 10001 \
    --no-create-home --shell /usr/sbin/nologin shadnet \
    && mkdir -p /opt/shadnet /var/lib/shadnet/db \
    && chown -R shadnet:shadnet /var/lib/shadnet
COPY --from=build /src/artifacts/ /opt/shadnet/
COPY --from=build /src/LICENSES/GPL-2.0-or-later.txt /usr/share/doc/shadnet/LICENSE
COPY entrypoint.sh /usr/local/bin/shadnet-entrypoint
RUN chmod 755 /usr/local/bin/shadnet-entrypoint
USER shadnet
WORKDIR /var/lib/shadnet
VOLUME ["/var/lib/shadnet"]
EXPOSE 31313/tcp 31314/udp 31315/tcp
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:31315/status >/dev/null || exit 1
ENTRYPOINT ["/usr/local/bin/shadnet-entrypoint"]
