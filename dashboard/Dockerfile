# ── Stage 1: build ──────────────────────────────────────────────────────────
FROM node:20-alpine AS build

WORKDIR /app

COPY package.json package-lock.json* ./
# --silent was hiding install errors. Keep verbose for first build, swap back later if noisy.
RUN npm ci --no-audit --no-fund

COPY . .
# CI=true silences interactive prompts; we also avoid CRA treating warnings as errors here
# so the production build is not blocked by lint-level warnings.
RUN CI=true DISABLE_ESLINT_PLUGIN=true npm run build

# ── Stage 2: serve ──────────────────────────────────────────────────────────
FROM nginx:1.27-alpine AS serve

# Remove default nginx static assets
RUN rm -rf /usr/share/nginx/html/*

# Copy built React app
COPY --from=build /app/build /usr/share/nginx/html

# Copy custom nginx config
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 3000

CMD ["nginx", "-g", "daemon off;"]
