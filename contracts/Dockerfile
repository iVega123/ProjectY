FROM node:24-alpine
WORKDIR /contracts
COPY . .
USER node
ENTRYPOINT ["node", "/contracts/register.mjs"]
