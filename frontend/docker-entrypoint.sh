#!/bin/sh
echo "window.__CONFIG__ = { API_URL: \"${API_URL:-http://localhost:8080}\" };" > /usr/share/nginx/html/config.js
