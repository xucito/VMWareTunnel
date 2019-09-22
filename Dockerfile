FROM mcr.microsoft.com/dotnet/core/aspnet:2.2-stretch-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 5002-5004

FROM mcr.microsoft.com/dotnet/core/sdk:2.2-stretch AS build
WORKDIR /src
COPY ["CloudOSTunnel/CloudOSTunnel.csproj", "CloudOSTunnel/"]
COPY ["FxSsh/FxSsh/FxSsh.csproj", "FxSsh/FxSsh/"]
RUN dotnet restore "CloudOSTunnel/CloudOSTunnel.csproj"
COPY . .
WORKDIR "/src/CloudOSTunnel"
RUN dotnet build "CloudOSTunnel.csproj" -c Release -o /app

FROM build AS publish
RUN dotnet publish "CloudOSTunnel.csproj" -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
COPY --from=build /src/CloudOSTunnel/Certificates/*.pfx ./Certificates/
ENTRYPOINT ["dotnet", "CloudOSTunnel.dll"]