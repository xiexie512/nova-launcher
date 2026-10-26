# NovaNet Hub —— 自研虚拟局域网协调服务器
# 构建: docker build -t novanet-hub .
# 运行: docker run -d --name novanet-hub --restart unless-stopped \
#         -p 8535:8535/tcp -p 8535:8535/udp novanet-hub
# 注意: TCP 与 UDP 8535 都要映射（TCP=WS 信令/中继，UDP=STUN 打洞探测）

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NovaNetHub.csproj .
COPY Program.cs HubServer.cs RoomManager.cs Room.cs StunServer.cs ./
COPY ../app/NovaNet.Engine/NovaNet.Engine.csproj /src/engine/NovaNet.Engine.csproj
COPY ../app/NovaNet.Engine/*.cs /src/engine/
RUN dotnet publish NovaNetHub.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=
EXPOSE 8535/tcp 8535/udp
ENTRYPOINT ["dotnet", "NovaNetHub.dll"]
