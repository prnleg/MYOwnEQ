# Build from the repo root:  docker build -f server/Dockerfile -t myowneq-server .
FROM rust:1-bookworm AS build
RUN apt-get update && apt-get install -y --no-install-recommends libssl-dev pkg-config && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN cargo build --release -p myowneq-server

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends libssl3 ca-certificates && rm -rf /var/lib/apt/lists/* \
 && useradd --system --uid 10001 myowneq && mkdir /data && chown myowneq /data
COPY --from=build /src/target/release/myowneq-server /usr/local/bin/myowneq-server
USER myowneq
ENV MYOWNEQ_DB=/data/myowneq.db \
    MYOWNEQ_BIND=0.0.0.0:7878
EXPOSE 7878
VOLUME /data
CMD ["myowneq-server"]
