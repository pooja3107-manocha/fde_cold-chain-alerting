# Runs the whole POC with only Docker installed:
#   docker build -t nimbus-coldchain .
#   docker run --rm -v "$PWD/out:/app/out" nimbus-coldchain              (all)
#   docker run --rm -v "$PWD/out:/app/out" nimbus-coldchain scenarios
#   docker run --rm -p 5080:5080 nimbus-coldchain dashboard              (then open http://localhost:5080)

# ---- Stage 1: build the React dashboard UI
FROM node:22-alpine AS ui
WORKDIR /ui
COPY ui-src/package.json ui-src/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY ui-src/ ./
RUN npm run build -- --outDir /ui/dist

# ---- Stage 2: engine, runner and dashboard API
FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /app
COPY . .
COPY --from=ui /ui/dist src/ColdChain.Dashboard/wwwroot
RUN dotnet build src/ColdChain.Runner -c Release \
 && dotnet publish src/ColdChain.Dashboard -c Release -o /app/dashboard
# Strip Windows line endings in case the repo was checked out on Windows.
RUN sed -i 's/\r$//' docker-entrypoint.sh && chmod +x docker-entrypoint.sh
EXPOSE 5080
ENTRYPOINT ["/app/docker-entrypoint.sh"]
CMD ["all"]
