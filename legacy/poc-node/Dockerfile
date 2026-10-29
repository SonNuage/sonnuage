FROM node:22-bookworm-slim

RUN apt-get update \
  && apt-get install -y --no-install-recommends ffmpeg \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY server.js index.html styles.css script.js config.js ./

ENV PORT=3000
EXPOSE 3000

CMD ["node", "server.js"]
