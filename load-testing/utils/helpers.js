import http from 'k6/http';

/**
 * Authentication helper for k6 load tests
 */
export function getAuthToken(authUrl, username, password) {
  const loginRes = http.post(`${authUrl}/token`, {
    grant_type: 'password',
    username: username,
    password: password,
  }, {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  });
  
  if (loginRes.status === 200) {
    return {
      accessToken: loginRes.json('access_token'),
      refreshToken: loginRes.json('refresh_token'),
      expiresIn: loginRes.json('expires_in'),
    };
  }
  
  return null;
}

/**
 * Refresh access token
 */
export function refreshAccessToken(authUrl, refreshToken) {
  const refreshRes = http.post(`${authUrl}/token`, {
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
  }, {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  });
  
  if (refreshRes.status === 200) {
    return {
      accessToken: refreshRes.json('access_token'),
      refreshToken: refreshRes.json('refresh_token'),
      expiresIn: refreshRes.json('expires_in'),
    };
  }
  
  return null;
}

/**
 * Create API headers with authorization
 */
export function createAuthHeaders(accessToken) {
  return {
    Authorization: `Bearer ${accessToken}`,
    'Content-Type': 'application/json',
  };
}

/**
 * Make authenticated GET request
 */
export function authenticatedGet(url, accessToken) {
  const headers = createAuthHeaders(accessToken);
  return http.get(url, { headers });
}

/**
 * Make authenticated POST request
 */
export function authenticatedPost(url, payload, accessToken) {
  const headers = createAuthHeaders(accessToken);
  return http.post(url, JSON.stringify(payload), { headers });
}

/**
 * Generate random test data
 */
export function generateRandomString(length = 10) {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let result = '';
  for (let i = 0; i < length; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return result;
}

/**
 * Generate random integer
 */
export function generateRandomInt(min, max) {
  return Math.floor(Math.random() * (max - min + 1)) + min;
}

/**
 * Generate timestamp-based unique ID
 */
export function generateUniqueId() {
  return `${Date.now()}-${Math.floor(Math.random() * 1000000)}`;
}
