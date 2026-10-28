# Optional container deploy for Nova Globen Stomme (the primary VM path is a published bundle + systemd; see README).
#
# Two-stage: publish the FRAMEWORK-DEPENDENT app on the SDK image, then run it on the ASP.NET runtime image.
# BLAZOR106 note: `dotnet publish` is incremental and never cleans; do not add `dotnet clean` here.
#
# Build from the REPO ROOT so the whole source tree is in context:
#   docker build -f setup/Dockerfile -t stomme:latest .
# Run with your config + generated OpenIddict cert mounted:
#   docker run -p 8080:8080 \
#     -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
#     -v $PWD/appsettings.Production.json:/app/appsettings.Production.json:ro \
#     -v $PWD/certs:/app/certs:ro stomme:latest

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Stomme.Server/Stomme.Server.csproj -c Release --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
COPY setup/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh && useradd -u 1000 -m stomme && chown -R stomme /app
USER stomme
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# The port the HEALTHCHECK calls. Change it together with ASPNETCORE_URLS if you run on another port.
ENV STOMME_HEALTH_PORT=8080
EXPOSE 8080

# Liveness only (BL-83 / D104): /healthz answers 200 "alive" whenever the process serves requests. NOT /readyz - on a
# fresh, not-yet-installed deployment /readyz is 503 (measured), so a readiness check would restart-loop the container
# before its web installer could ever run. The runtime image has no curl or wget, so the probe is bash's own /dev/tcp:
# a raw GET that passes only on a 200 status line (validated against a live 200, a live 503 and a closed port).
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/${STOMME_HEALTH_PORT} && printf "GET /healthz HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "'

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
