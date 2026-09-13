FROM node:26-alpine
WORKDIR /contracts
COPY . .
USER node
ENTRYPOINT ["node", "/contracts/register.mjs"]
