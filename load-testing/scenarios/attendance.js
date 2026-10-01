import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');
const attendanceSubmitDuration = new Trend('attendance_submit_duration');
const attendanceCounter = new Counter('attendance_count');

// Configuration
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8086';
const API_URL = __ENV.API_URL || 'http://localhost:8087';

export const options = {
  scenarios: {
    // Scenario: Attendance submission load
    attendance_load: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '1m', target: 30 },
        { duration: '5m', target: 30 },
        { duration: '1m', target: 100 },
        { duration: '5m', target: 100 },
        { duration: '1m', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_duration: ['p(95)<500'],   // 95% under 500ms
    http_req_failed: ['rate<0.01'],     // Less than 1% errors
    errors: ['rate<0.01'],
    attendance_submit_duration: ['p(95)<1000'],
  },
};

// Test users
const TEST_USERS = [
  { username: 'e2e.superadmin@scube.test', password: 'E2e-Test-2026!' },
  { username: 'e2e.campus@scube.test', password: 'E2e-Test-2026!' },
];

// Attendance statuses
const STATUSES = ['Present', 'Absent', 'Late', 'Excused'];

export function setup() {
  console.log('Starting attendance load test...');
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
  // ATTENDANCE SUBMISSION FLOW
  // ============================================================
  
  // Step 1: Get classroom students
  const studentsRes = http.get(`${API_URL}/api/1/1/1/studentEnrollment/list?classroomId=1`, {
    headers,
  });
  
  check(studentsRes, {
    'students list status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Step 2: Submit attendance for each student
  const attendanceStart = Date.now();
  
  const attendancePayload = {
    classroomId: 1,
    subjectId: 1,
    attendanceDate: new Date().toISOString().split('T')[0],
    records: [
      { studentId: 1, status: 'Present', remarks: '' },
      { studentId: 2, status: 'Absent', remarks: 'Absent without notice' },
      { studentId: 3, status: 'Late', remarks: 'Arrived 10 minutes late' },
    ],
    tenantId: 1,
    schoolId: 1,
    campusId: 1,
  };
  
  const attendanceRes = http.post(`${API_URL}/api/1/1/1/attendance`, 
    JSON.stringify(attendancePayload), 
    { headers }
  );
  
  const attendanceSuccess = check(attendanceRes, {
    'attendance submit status is 200 or 400': (r) => r.status === 200 || r.status === 400,
    'attendance has response': (r) => r.body.length > 0,
  });
  
  attendanceSubmitDuration.add(Date.now() - attendanceStart);
  errorRate.add(!attendanceSuccess);
  
  if (attendanceSuccess) {
    attendanceCounter.add(1);
  }
  
  sleep(1);
  
  // ============================================================
  // ATTENDANCE READ OPERATIONS
  // ============================================================
  
  // Get attendance list
  const attendanceListRes = http.get(`${API_URL}/api/1/1/1/attendance/list`, {
    headers,
  });
  
  check(attendanceListRes, {
    'attendance list status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Get attendance by date
  const today = new Date().toISOString().split('T')[0];
  const attendanceByDateRes = http.get(`${API_URL}/api/1/1/1/attendance/list?date=${today}`, {
    headers,
  });
  
  check(attendanceByDateRes, {
    'attendance by date status is 200': (r) => r.status === 200,
  });
  
  sleep(1);
}

export function teardown(data) {
  console.log('Attendance load test completed');
}
