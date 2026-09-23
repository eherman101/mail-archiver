# Takeout importer: Mail-Archiver's own image plus Python, so the CLI importer it
# runs is always the same build as the running app. Rebuild after rebuilding
# mail-archiver-local:latest.
#
#   cd ~/claude-code/mail-archiver/takeout-importer && sudo docker build -t mail-archiver-takeout:latest .
FROM mail-archiver-local:latest

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*

COPY importer.py /opt/takeout-importer/importer.py

ENTRYPOINT ["python3", "-u", "/opt/takeout-importer/importer.py"]
