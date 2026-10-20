FROM python:3.12-slim

ARG PIP_INDEX_URL=https://pypi.org/simple
RUN python -m pip install --no-cache-dir --index-url "$PIP_INDEX_URL" Pillow==13.0.0 \
    && groupadd --gid 10001 forum \
    && useradd --uid 10001 --gid forum --no-create-home forum \
    && mkdir -p /app /var/lib/forum \
    && chown forum:forum /var/lib/forum

COPY server.py /app/server.py
USER forum
ENV PYTHONDONTWRITEBYTECODE=1 FORUM_DATA=/var/lib/forum PORT=8080
WORKDIR /app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD python -c "from urllib.request import urlopen; urlopen('http://127.0.0.1:8080/healthz', timeout=3).close()"
CMD ["python", "/app/server.py"]
