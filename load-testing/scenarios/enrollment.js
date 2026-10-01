import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');
const enrollmentDuration = new Trend('enrollment_duration');
const enrollmentCounter = new Counter('enrollment_count');

// Configuration
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8086';
const API_URL = __ENV.API_URL || 'http://localhost:8087';

export const options = {
  scenarios: {
    // Scenario: Enrollment load test
    enrollment_load: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '1m', target: 20 },
        { duration: '5m', target: 20 },
        { duration: '1m', target: 50 },
        { duration: '5m', target: 50 },
        { duration: '1m', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<1000'],  // 95% under 1s
    http_req_failed: ['rate<0.02'],     // Less than 2% errors
    errors: ['rate<0.02'],
    enrollment_duration: ['p(95)<2000'],
  },
};

// Test users
const TEST_USERS = [
  { username: 'e2e.superadmin@scube.test', password: 'E2e-Test-2026!' },
  { username: 'e2e.campus@scube.test', password: 'E2e-Test-2026!' },
];

export function setup() {
  console.log('Starting enrollment load test...');
}

function getAuthToken(user) {
  const loginRes = http.post(`${BASE_URL}/token`, {
    grant_type: 'password',
    username: user.username,
    password: user.password,
  }, {
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
  });
  
  if (loginRes.status === 200) {
    return loginRes.json('access_token');
  }
  return null;
}

export default function () {
  const user = TEST_USERS[__VU % TEST_USERS.length];
  const token = getAuthToken(user);
  
  if (!token) {
    console.log('Login failed');
    errorRate.add(true);
    sleep(1);
    return;
  }
  
  const headers = { 
    Authorization: `Bearer ${token}`,
    'Content-Type': 'application/json',
  };
  
  // ============================================================
  // ENROLLMENT FLOW
  // ============================================================
  
  // Step 1: Get available students
  const availableStudentsRes = http.get(`${API_URL}/api/1/1/1/student/available/1`, {
    headers,
  });
  
  check(availableStudentsRes, {
    'available students status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Step 2: Get fee structure
  const feeStructureRes = http.get(`${API_URL}/api/1/1/1/feeStructure/activeByYearGrade/1/1`, {
    headers,
  });
  
  check(feeStructureRes, {
    'fee structure status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Step 3: Enroll student
  const enrollmentStart = Date.now();
  
  // Generate unique student data
  const timestamp = Date.now();
  const randomId = Math.floor(Math.random() * 1000000);
  
  const enrollmentPayload = {
    studentId: randomId,
    classroomId: 1,
    academicYearId: 1,
    tenantId: 1,
    schoolId: 1,
    campusId: 1,
  };
  
  const enrollmentRes = http.post(`${API_URL}/api/1/1/1/studentEnrollment`, 
    JSON.stringify(enrollmentPayload), 
    { headers }
  );
  
  const enrollmentSuccess = check(enrollmentRes, {
    'enrollment status is 200 or 400': (r) => r.status === 200 || r.status === 400,
    'enrollment has response': (r) => r.body.length > 0,
  });
  
  enrollmentDuration.add(Date.now() - enrollmentStart);
  errorRate.add(!enrollmentSuccess);
  
  if (enrollmentSuccess) {
    enrollmentCounter.add(1);
  }
  
  sleep(1);
  
  // ============================================================
  // READ OPERATIONS (simulate user browsing)
  // ============================================================
  
  // Get enrolled students
  const enrolledRes = http.get(`${API_URL}/api/1/1/1/studentEnrollment/list`, {
    headers,
  });
  
  check(enrolledRes, {
    'enrolled list status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Get classroom details
  const classroomRes = http.get(`${API_URL}/api/1/1/1/classroom/1`, {
    headers,
  });
  
  check(classroomRes, {
    'classroom details status is 200': (r) => r.status === 200,
  });
  
  sleep(1);
}

export function teardown(data) {
  console.log('Enrollment load test completed');
}
