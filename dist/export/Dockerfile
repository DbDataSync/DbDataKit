# The export layer (plan 32 DBR16): a Gateway image that can export PDFs. It adds to a built image the shared libraries Chromium needs, the host font packages an
# administrator promises (none by default), and the export component (the pinned, hash-checked Chromium, the bundled fonts with their licences and the print page).
#   python3 dist/export_component.py stage --rid linux-x64 --out .export-component        (after scripts/fetch-chromium.sh, scripts/fetch-fonts.sh and npm run build:print)
#   docker build -f dist/export/Dockerfile --build-arg BASE_IMAGE=dbdatagateway -t dbdatagateway-export .      (from the repository root)
#   --build-arg HOST_FONT_PACKAGES="fonts-noto-cjk"     only for host fonts the image promises in views.export.fonts.required; a host that promises none installs none
# The layer is a separate image so the base stays small (the browser is about 420 MB installed). Debian packages only: the image of the Gateway is Debian, and the layer
# does not run a package manager outside apt. The build needs a package mirror (a proxy that blocks one fails here, naming the package).
ARG BASE_IMAGE=dbdatagateway
FROM ${BASE_IMAGE}
USER root
ARG HOST_FONT_PACKAGES=""
# The libraries Chromium links against (the names are virtual on the newer Debian that renamed some with a t64 suffix: apt picks the provider).
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       ca-certificates fontconfig libasound2 libatk-bridge2.0-0 libatk1.0-0 libcairo2 libcups2 libdbus-1-3 libdrm2 libexpat1 libgbm1 libglib2.0-0 libnspr4 libnss3 \
       libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 $HOST_FONT_PACKAGES \
    && rm -rf /var/lib/apt/lists/*
COPY .export-component /opt/dbdatakit/export
# A browser that cannot start (a library missing) fails the build, not the first export.
RUN browser="$(ls /opt/dbdatakit/export/.chromium/linux-x64/chrome-linux64/chrome)" \
    && chmod +x "$browser" \
    && if ldd "$browser" | grep -q "not found"; then ldd "$browser" | grep "not found"; exit 1; fi \
    && "$browser" --version
# Export is on in this image: the pinned browser, the print page and the bundled fonts of the component. A container forces --no-sandbox (it is logged).
ENV DBDATAKIT_Gateway__Views__Export__Mode=pinned \
    DBDATAKIT_Gateway__Views__Export__Pin=/opt/dbdatakit/export/chromium.pin \
    DBDATAKIT_Gateway__Views__Export__PageAssets=/opt/dbdatakit/export/print \
    DBDATAKIT_Gateway__Views__Export__Fonts__Directory=/opt/dbdatakit/export/.fonts \
    DBDATAKIT_Gateway__Views__Export__Fonts__Pin=/opt/dbdatakit/export/fonts.pin \
    DBDATAKIT_Gateway__Views__Export__NoSandbox=true
