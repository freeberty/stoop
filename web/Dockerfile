FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
ARG VITE_API_URL=http://localhost:5080
ARG VITE_MAP_STYLE_URL=https://tiles.openfreemap.org/styles/liberty
ARG VITE_NOSTR_DOMAIN=localhost
ENV VITE_API_URL=$VITE_API_URL VITE_MAP_STYLE_URL=$VITE_MAP_STYLE_URL VITE_NOSTR_DOMAIN=$VITE_NOSTR_DOMAIN
RUN npm run build

FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
