# Synthetic acceptance fixture only; never a production broker/ingress image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
FROM rabbitmq:4-management
COPY --from=runtime /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_ROOT=/usr/share/dotnet PATH="/usr/share/dotnet:${PATH}" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 ASPNETCORE_URLS=http://+:8080
COPY publish/ /app/
WORKDIR /app
COPY fixture-entrypoint.sh /fixture-entrypoint.sh
RUN chmod +x /fixture-entrypoint.sh && printf 'listeners.tcp.default = 127.0.0.1:5672\nmanagement.tcp.ip = 127.0.0.1\n' > /etc/rabbitmq/conf.d/99-local.conf
EXPOSE 8080
ENTRYPOINT ["/fixture-entrypoint.sh"]
