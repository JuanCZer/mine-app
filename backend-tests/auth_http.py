"""Explicit Testing-only UUID/role identity with real antiforgery protection."""
import http.cookiejar
import json
import urllib.request

TEST_USER = '11111111-1111-4111-8111-111111111111'

def authenticated_request(url, method, path, body=None, user=TEST_USER, role='validador'):
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    headers = {'Content-Type': 'application/json', 'X-Test-User': user, 'X-Test-Roles': role}
    if method not in ('GET', 'HEAD'):
        with opener.open(urllib.request.Request(url + '/api/auth/csrf', headers=headers), timeout=10):
            pass
        headers['X-XSRF-TOKEN'] = next(cookie.value for cookie in jar if cookie.name == 'XSRF-TOKEN')
    request = urllib.request.Request(url + path, data=json.dumps(body).encode() if body is not None else None,
                                     method=method, headers=headers)
    return opener.open(request, timeout=10)
