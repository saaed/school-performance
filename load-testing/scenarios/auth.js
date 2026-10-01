import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');
const loginDuration = new Trend('login_duration');
const tokenRefreshDuration = new Trend('token_refresh_duration');

// Configuration
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8086';
const API_URL = __ENV.API_URL || 'http://localhost:8087';

export const options = {
  scenarios: {
    // Scenario 1: Constant load
    constant_load: {
      executor: 'constant-vus',
      vus: 50,
      duration: '5m',
    },
    // Scenario 2: Ramp up
    ramping_load: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '2m', target: 100 },
        { duration: '5m', target: 100 },
        { duration: '2m', target: 200 },
        { duration: '5m', target: 200 },
        { duration: '2m', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<500'],  // 95% of requests under 500ms
    http_req_failed: ['rate<0.01'],    // Less than 1% errors
    errors: ['rate<0.01'],
    login_duration: ['p(95)<1000'],
  },
};

// Test users (from your e2e baseline)
const TEST_USERS = [
  { username: 'e2e.superadmin@scube.test', password: 'E2e-Test-2026!', role: 'SuperAdmin' },
  { username: 'e2e.tenant@scube.test', password: 'E2e-Test-2026!', role: 'Tenant' },
  { username: 'e2e.school@scube.test', password: 'E2e-Test-2026!', role: 'School' },
  { username: 'e2e.campus@scube.test', password: 'E2e-Test-2026!', role: 'Campus' },
];

export function setup() {
  console.log('Starting authentication load test...');
  console.log(`Auth URL: ${BASE_URL}`);
  console.log(`API URL: ${API_URL}`);
}

export default function () {
  const user = TEST_USERS[__VU % TEST_USERS.length];
  
  // ============================================================
  // LOGIN FLOW
  // ============================================================
  
  // Step 1: Login
  const loginStart = Date.now();
  const loginRes = http.post(`${BASE_URL}/token`, {
    grant_type: 'password',
    username: user.username,
    password: user.password,
  }, {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  });
  
  const loginSuccess = check(loginRes, {
    'login status is 200': (r) => r.status === 200,
    'login has access_token': (r) => r.json('access_token') !== undefined,
    'login has refresh_token': (r) => r.json('refresh_token') !== undefined,
  });
  
  loginDuration.add(Date.now() - loginStart);
  errorRate.add(!loginSuccess);
  
  if (!loginSuccess) {
    console.log(`Login failed for ${user.username}: ${loginRes.status} ${loginRes.body}`);
    sleep(1);
    return;
  }
  
  const accessToken = loginRes.json('access_token');
  const refreshToken = loginRes.json('refresh_token');
  
  // Step 2: Check for scope selection (multi-scope users)
  const scopeCheckRes = http.get(`${API_URL}/api/account/scopes`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  
  let selectedScope = null;
  if (scopeCheckRes.status === 200 && scopeCheckRes.json('requiresSelection')) {
    const scopes = scopeCheckRes.json('scopes');
    if (scopes && scopes.length > 0) {
      selectedScope = scopes[0];
    }
  }
  
  // Step 3: Verify token works
  const verifyRes = http.get(`${API_URL}/api/1/1/1/student/list`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  
  check(verifyRes, {
    'token verification status is 200': (r) => r.status === 200,
  });
  
  sleep(1);
  
  // ============================================================
  // REFRESH TOKEN FLOW
  // ============================================================
  
  const refreshStart = Date.now();
  const refreshRes = http.post(`${BASE_URL}/token`, {
    grant_type: 'refresh_token',
    refresh_token: refreshToken,
  }, {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  });
  
  const refreshSuccess = check(refreshRes, {
    'refresh status is 200': (r) => r.status === 200,
    'refresh has new access_token': (r) => r.json('access_token') !== undefined,
  });
  
  tokenRefreshDuration.add(Date.now() - refreshStart);
  errorRate.add(!refreshSuccess);
  
  if (refreshSuccess) {
    const newAccessToken = refreshRes.json('access_token');
    
    // Use the new token for subsequent requests
    const apiHeaders = { Authorization: `Bearer ${newAccessToken}` };
    
    // ============================================================
    // API CALLS (simulate real user behavior)
    // ============================================================
    
    // Get student list
    const studentListRes = http.get(`${API_URL}/api/1/1/1/student/list`, {
      headers: apiHeaders,
    });
    
    check(studentListRes, {
      'student list status is 200': (r) => r.status === 200,
      'student list has data': (r) => r.json('count') !== undefined,
    });
    
    sleep(1);
    
    // Get attendance list
    const attendanceRes = http.get(`${API_URL}/api/1/1/1/attendance/list`, {
      headers: apiHeaders,
    });
    
    check(attendanceRes, {
      'attendance list status is 200': (r) => r.status === 200,
    });
    
    sleep(1);
    
    // Get invoice list
    const invoiceRes = http.get(`${API_URL}/api/1/1/1/invoice/list`, {
      headers: apiHeaders,
    });
    
    check(invoiceRes, {
      'invoice list status is 200': (r) => r.status === 200,
    });
    
    sleep(1);
  }
  
  sleep(1);
}

export function teardown(data) {
  console.log('Authentication load test completed');
}
