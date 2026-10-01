import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

// Custom metrics
const errorRate = new Rate('errors');
const invoiceCreateDuration = new Trend('invoice_create_duration');
const paymentDuration = new Trend('payment_duration');
const invoiceCounter = new Counter('invoice_count');
const paymentCounter = new Counter('payment_count');

// Configuration
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8086';
const API_URL = __ENV.API_URL || 'http://localhost:8087';

export const options = {
  scenarios: {
    // Scenario: Fee/Payment load test
    fee_payment_load: {
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
    invoice_create_duration: ['p(95)<2000'],
    payment_duration: ['p(95)<2000'],
  },
};

// Test users
const TEST_USERS = [
  { username: 'e2e.superadmin@scube.test', password: 'E2e-Test-2026!' },
  { username: 'e2e.campus@scube.test', password: 'E2e-Test-2026!' },
];

export function setup() {
  console.log('Starting fee/payment load test...');
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
  // INVOICE CREATION FLOW
  // ============================================================
  
  // Step 1: Get fee types
  const feeTypesRes = http.get(`${API_URL}/api/1/1/1/feeType/list`, {
    headers,
  });
  
  check(feeTypesRes, {
    'fee types status is 200': (r) => r.status === 200,
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
  
  // Step 3: Create invoice
  const invoiceStart = Date.now();
  
  const invoicePayload = {
    studentId: 1,
    feeTypeId: 1,
    academicYearId: 1,
    totalAmount: 5000,
    dueDate: new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString().split('T')[0],
    tenantId: 1,
    schoolId: 1,
    campusId: 1,
  };
  
  const invoiceRes = http.post(`${API_URL}/api/1/1/1/invoice`, 
    JSON.stringify(invoicePayload), 
    { headers }
  );
  
  const invoiceSuccess = check(invoiceRes, {
    'invoice create status is 200 or 400': (r) => r.status === 200 || r.status === 400,
    'invoice has response': (r) => r.body.length > 0,
  });
  
  invoiceCreateDuration.add(Date.now() - invoiceStart);
  errorRate.add(!invoiceSuccess);
  
  if (invoiceSuccess) {
    invoiceCounter.add(1);
  }
  
  sleep(1);
  
  // ============================================================
  // PAYMENT PROCESSING FLOW
  // ============================================================
  
  // Step 4: Get invoice list
  const invoiceListRes = http.get(`${API_URL}/api/1/1/1/invoice/list`, {
    headers,
  });
  
  check(invoiceListRes, {
    'invoice list status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Step 5: Process payment
  const paymentStart = Date.now();
  
  const paymentPayload = {
    invoiceId: 1,
    amount: 2500,
    paymentMethod: 'Cash',
    referenceNumber: `PAY-${Date.now()}-${Math.floor(Math.random() * 1000000)}`,
    tenantId: 1,
    schoolId: 1,
    campusId: 1,
  };
  
  const paymentRes = http.post(`${API_URL}/api/1/1/1/payment`, 
    JSON.stringify(paymentPayload), 
    { headers }
  );
  
  const paymentSuccess = check(paymentRes, {
    'payment status is 200 or 400': (r) => r.status === 200 || r.status === 400,
    'payment has response': (r) => r.body.length > 0,
  });
  
  paymentDuration.add(Date.now() - paymentStart);
  errorRate.add(!paymentSuccess);
  
  if (paymentSuccess) {
    paymentCounter.add(1);
  }
  
  sleep(1);
  
  // ============================================================
  // READ OPERATIONS
  // ============================================================
  
  // Get student dues
  const duesRes = http.get(`${API_URL}/api/1/1/1/invoice/studentDues/1`, {
    headers,
  });
  
  check(duesRes, {
    'student dues status is 200': (r) => r.status === 200,
  });
  
  sleep(0.5);
  
  // Get payment history
  const paymentHistoryRes = http.get(`${API_URL}/api/1/1/1/payment/list`, {
    headers,
  });
  
  check(paymentHistoryRes, {
    'payment history status is 200': (r) => r.status === 200,
  });
  
  sleep(1);
}

export function teardown(data) {
  console.log('Fee/Payment load test completed');
}
