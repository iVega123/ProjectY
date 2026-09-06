FROM node:24-alpine AS development
RUN apk upgrade --no-cache
WORKDIR /workspace
COPY package*.json ./
RUN npm ci
COPY . .
ENV NEXT_TELEMETRY_DISABLED=1
CMD ["npm", "run", "dev"]

FROM development AS builder
RUN npm run build

FROM node:24-alpine AS final
RUN apk upgrade --no-cache
WORKDIR /app
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 HOSTNAME=0.0.0.0 PORT=3001
COPY --from=builder --chown=node:node /workspace/.next/standalone ./
COPY --from=builder --chown=node:node /workspace/.next/static ./.next/static
USER node
EXPOSE 3001
CMD ["node", "server.js"]
